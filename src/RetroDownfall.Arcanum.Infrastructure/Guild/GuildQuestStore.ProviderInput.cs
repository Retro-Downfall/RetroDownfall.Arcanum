using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal IDisposable EnterProviderInvocation(GuildLocalInferenceExecution execution) =>
        GuildMemoryInvocationAmbient.Begin(new(authority, execution.Driver.Execution));

    internal Task<Result<GuildLocalInferenceMaterialization>> MaterializeProviderInputAsync(
        GuildLocalInferenceExecution execution, GuildLocalExecutionInput input, CancellationToken ct)
    {
        if (execution is null || !ReferenceEquals(execution.Store, this) || !DriverShape(execution.Driver) || input is null)
            return Task.FromResult(Result<GuildLocalInferenceMaterialization>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildLocalInferenceMaterialization>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (policy is GuildLocalHostPolicy { IsExecutionEnabled: false }) return Unavailable;

                GuildQuestDriver driver = execution.Driver;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, execution.Dispatch.DispatchId, token).ConfigureAwait(false);

                if (dispatch.IsFailure) return dispatch.Error;

                if (dispatch.Value is null || !SameProviderDispatch(dispatch.Value, execution.Dispatch)
                    || dispatch.Value.QuestId != driver.Execution.QuestId || dispatch.Value.AccountingAssociationId != driver.Execution.RunId
                    || dispatch.Value.Evidence == GuildDispatchEvidence.ResolvedNoDispatch) return Conflict;

                return await MaterializeProviderInputWithinAsync(connection, transaction, admitted.Value.Row, input, dispatch.Value, token).ConfigureAwait(false);
            }, ct);
    }

    private static async Task<Result<GuildLocalInferenceMaterialization>> MaterializeProviderInputWithinAsync(
        SqliteConnection connection, SqliteTransaction transaction, GuildQuestStored quest,
        GuildLocalExecutionInput expected, GuildQuestDispatchRecord dispatch, CancellationToken ct)
    {
        Result<GuildLocalExecutionBinding> binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction,
            quest.Quest.Id, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        Result<GuildLocalExecutionInput> accepted = await GuildLocalExecutionStore.ReadInputWithinAsync(connection,
            transaction, quest.Quest.Id, null, ct).ConfigureAwait(false);

        if (accepted.IsFailure) return accepted.Error;

        byte[] expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected, GuildStorageJsonContext.Default.GuildLocalExecutionInput);

        if (expectedBytes.Length > GuildStore.MaximumContentBytes || !expectedBytes.AsSpan().SequenceEqual(
            JsonSerializer.SerializeToUtf8Bytes(accepted.Value, GuildStorageJsonContext.Default.GuildLocalExecutionInput))) return Conflict;

        if (accepted.Value.Goal != quest.Quest.GoalRef) return Conflict;

        Result<string> goal = await ReadProviderContentWithinAsync(connection, transaction, accepted.Value.Goal, ct).ConfigureAwait(false);

        if (goal.IsFailure) return goal.Error;

        ImmutableArray<GuildLocalInferenceFile>.Builder files = ImmutableArray.CreateBuilder<GuildLocalInferenceFile>();

        foreach (GuildLocalExecutionFile file in accepted.Value.Files)
        {
            Result<string> body = await ReadProviderContentWithinAsync(connection, transaction, file.Content, ct).ConfigureAwait(false);

            if (body.IsFailure) return body.Error;

            files.Add(new(file.Name, body.Value));
        }

        if (accepted.Value.Card is { } selected)
        {
            if (selected.CardId != quest.Quest.CardId || selected.Revision != quest.Quest.CardRevision) return Conflict;

            await using SqliteCommand pinned = Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM guild_agent_card_revisions r JOIN guild_agent_cards a ON a.CardId=r.CardId
                    WHERE r.CardId=$card AND r.Revision=$revision AND r.ContentId=$content
                        AND a.AgentId=$agent AND a.SourceKindCode<>3)
                """);

            Add(pinned, "$card", Id(selected.CardId));

            Add(pinned, "$revision", selected.Revision);

            Add(pinned, "$content", Id(selected.Content.ContentId));

            Add(pinned, "$agent", Id(quest.Quest.AgentId));

            if (!Equals(1L, await pinned.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Conflict;

            var body = await ReadProviderContentWithinAsync(connection, transaction, selected.Content, ct).ConfigureAwait(false);

            if (body.IsFailure) return body.Error;

            GuildStoredAgentCardPayload? payload;

            try { payload = JsonSerializer.Deserialize(body.Value, GuildStorageJsonContext.Default.GuildStoredAgentCardPayload); }
            catch (JsonException) { return Unavailable; }

            if (payload is null || payload.Card is null || payload.Guidance is null || payload.Card.CardId != selected.CardId
                || payload.Card.Revision != selected.Revision || payload.Card.AgentId != quest.Quest.AgentId) return Conflict;

            files.Add(new($"Agent Card {selected.CardId:D} revision {selected.Revision}", payload.Guidance));
        }

        GuildAuthorityKind goalAuthority = quest.Quest.ParentQuestId is null ? GuildAuthorityKind.Operator : GuildAuthorityKind.Parent;

        await using (SqliteCommand provenance = Command(connection, transaction,
            "SELECT PeerId FROM guild_signets WHERE QuestId=$quest AND PeerId IS NOT NULL"))
        {
            Add(provenance, "$quest", Id(quest.Quest.Id));

            if (await provenance.ExecuteScalarAsync(ct).ConfigureAwait(false) is string) goalAuthority = GuildAuthorityKind.Parent;
        }

        return new GuildLocalInferenceMaterialization(goal.Value, files.ToImmutable(), binding.Value, dispatch)
        {
            CampaignId = quest.Quest.CampaignId,
            GoalAuthority = goalAuthority,
        };
    }

    private static async Task<Result<string>> ReadProviderContentWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildDataRef reference, CancellationToken ct)
    {
        var validated = await GuildLocalExecutionStore.ValidateReferenceAsync(connection, transaction, reference, ct).ConfigureAwait(false);

        if (validated.IsFailure) return validated.Error;

        if (validated.Value is not null) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Protected Guild input requires a qualified disclosure adapter.");

        await using SqliteCommand read = Command(connection, transaction,
            "SELECT Body FROM guild_content WHERE ContentId=$id AND typeof(Body)='blob' AND length(Body)<=65536");

        Add(read, "$id", Id(reference.ContentId));

        if (await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] body || body.LongLength != reference.ByteLength
            || Convert.ToHexString(SHA256.HashData(body)) != reference.Digest) return Unavailable;

        try
        {
            return new UTF8Encoding(false, true).GetString(body);
        }
        catch (DecoderFallbackException)
        {
            return new Error(ErrorCodes.Guild.InvalidRequest, "The accepted provider input is not UTF-8 text.");
        }
    }

    /// <summary>The immutable result receipt transaction owns terminal run disposition.</summary>
    internal static async Task<Result> FinishProviderRunWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored quest, InferenceRunStatus status, CancellationToken ct)
    {
        if (status == InferenceRunStatus.Running || !Enum.IsDefined(status) || quest.Quest.SessionId is not Guid session) return InvalidReceipt;

        await using SqliteCommand read = Command(connection, transaction,
            "SELECT RequestId,SessionId,Surface,Purpose,Status FROM InferenceRuns WHERE Id=$run");

        Add(read, "$run", quest.Quest.RunId.ToString("N"));

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result.Success();

        if (reader.GetString(0) != quest.Quest.Id.ToString("N") || reader.GetString(1) != session.ToString("N")
            || reader.GetString(2) != "guild" || reader.GetString(3) != "quest") return Conflict;

        int current = reader.GetInt32(4);

        if (current == (int)status) return Result.Success();

        if (current != (int)InferenceRunStatus.Running) return Conflict;

        await reader.DisposeAsync().ConfigureAwait(false);

        await using SqliteCommand update = Command(connection, transaction,
            "UPDATE InferenceRuns SET Status=$status,CompletedAt=$now WHERE Id=$run AND Status=0");

        Add(update, "$status", (int)status);

        Add(update, "$now", UtcInstantText.Format(DateTimeOffset.UtcNow));

        Add(update, "$run", quest.Quest.RunId.ToString("N"));

        return await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Result.Failure(Conflict);
    }
}
