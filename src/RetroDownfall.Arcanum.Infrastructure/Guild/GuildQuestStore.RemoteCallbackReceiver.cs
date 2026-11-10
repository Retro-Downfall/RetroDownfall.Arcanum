using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildRemoteCallbackBinding(Guid QuestId, Guid RootQuestId, Guid DispatchId, string TokenHash);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildRemoteCallbackBinding?>> FindRemoteCallbackAsync(string configId, string? presented, CancellationToken ct) =>
        guild.FindRemoteCallbackAsync(configId, presented, ct);

    internal Task<Result<GuildQuestDto>> AcceptRemoteCallbackAsync(GuildCallerContext caller, string configId,
        string? presented, GuildRemoteCallbackBinding expected, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestDto>(expected.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                if (policy is not GuildLocalHostPolicy { IsExecutionEnabled: true }) return Unavailable;

                var binding = await GuildStore.ReadRemoteCallbackWithinAsync(connection, transaction, configId, presented, token).ConfigureAwait(false);

                if (binding.IsFailure) return binding.Error;

                if (binding.Value is null || binding.Value != expected) return Conflict;

                var row = await ReadWithinAsync(connection, transaction, expected.QuestId, token).ConfigureAwait(false);

                if (row.IsFailure) return row.Error;

                if (row.Value.Quest.RootQuestId != expected.RootQuestId) return Conflict;

                Result visible = await RequireVisibleAsync(connection, transaction, caller, row.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                var live = await signets.ValidateCurrentGrantAsync(expected.QuestId, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                var input = await ReadRemoteInvocationWithinAsync(connection, transaction, expected.QuestId, expected.DispatchId, token).ConfigureAwait(false);

                if (input.IsFailure) return input.Error;

                if (input.Value.Mode != A2ADispatchMode.Callback) return Conflict;

                await using (SqliteCommand latest = Command(connection, transaction, """
                    SELECT COALESCE((SELECT ContinuationId FROM guild_continuations WHERE QuestId=$quest
                        ORDER BY AcceptedRevision DESC LIMIT 1),$quest)
                    """))
                {
                    Add(latest, "$quest", Id(expected.QuestId));

                    if (!Equals(Id(expected.DispatchId), await latest.ExecuteScalarAsync(token).ConfigureAwait(false))) return Conflict;
                }

                var card = await GuildStore.ResolveQuestCardWithinAsync(connection, transaction, row.Value.Quest.AgentId,
                    row.Value.Quest.CardId, row.Value.Quest.CardRevision,
                    new(row.Value.Quest.WorkspaceId, row.Value.Quest.SessionId, row.Value.Quest.CampaignId), token).ConfigureAwait(false);

                if (card.IsFailure) return card.Error;

                GuildRemoteTransportDescriptor? accepted = card.Value.Payload.RemoteTransport;

                if (card.Value.Payload.Card.Source.Kind != GuildAgentSourceKind.Remote || accepted is null
                    || card.Value.Payload.Card.Source.PeerId != input.Value.Transport.PeerId
                    || card.Value.Payload.DiscoveryEndpoint != input.Value.DiscoveryEndpoint
                    || !JsonSerializer.SerializeToUtf8Bytes(accepted, GuildJsonContext.Default.GuildRemoteTransportDescriptor)
                        .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(input.Value.Transport, GuildJsonContext.Default.GuildRemoteTransportDescriptor)))
                    return Conflict;

                await using SqliteCommand receipt = Command(connection, transaction, """
                    UPDATE guild_remote_callbacks SET CallbackReceivedAtUtc=COALESCE(CallbackReceivedAtUtc,$now)
                    WHERE CallbackConfigId=$config AND QuestId=$quest AND DispatchId=$dispatch AND CallbackTokenHash=$hash
                    """);

                Add(receipt, "$now", UtcInstantText.Format(time.GetUtcNow()));

                Add(receipt, "$config", configId);

                Add(receipt, "$quest", Id(expected.QuestId));

                Add(receipt, "$dispatch", Id(expected.DispatchId));

                Add(receipt, "$hash", expected.TokenHash);

                if (await receipt.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;

                return row.Value.Quest;
            }, ct);
}

internal sealed partial class GuildStore
{
    internal async Task<Result<GuildRemoteCallbackBinding?>> FindRemoteCallbackAsync(string configId, string? presented, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return Deferred();

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        try
        {
            var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, lifetime.Token).ConfigureAwait(false);

            if (opened.IsFailure)
                return opened.Error.Code == ErrorCodes.Grimoire.MaintenanceUnavailable ? Deferred() : Unavailable();

            await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

            await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: true);

            // Guild is optional. Only an absent, undeclared callback schema permits independent
            // legacy lookup; a declared schema with missing objects must remain unavailable.
            await using SqliteCommand schema = Command(lease.Connection, transaction,
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='guild_remote_callbacks')");

            bool installed = Equals(1L, await schema.ExecuteScalarAsync(lifetime.Token).ConfigureAwait(false));

            if (!installed)
            {
                schema.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='grimoire_feature_schemas')";

                if (Equals(1L, await schema.ExecuteScalarAsync(lifetime.Token).ConfigureAwait(false)))
                {
                    schema.CommandText = "SELECT EXISTS(SELECT 1 FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3 AND SchemaVersion>=11)";

                    if (Equals(1L, await schema.ExecuteScalarAsync(lifetime.Token).ConfigureAwait(false))) return Unavailable();
                }

                await transaction.CommitAsync(lifetime.Token).ConfigureAwait(false);

                return Result<GuildRemoteCallbackBinding?>.Success(null);
            }

            Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, transaction, lifetime.Token).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            var result = await ReadRemoteCallbackWithinAsync(lease.Connection, transaction, configId, presented, lifetime.Token).ConfigureAwait(false);

            if (result.IsFailure) return result.Error;

            lifetime.Token.ThrowIfCancellationRequested();

            await transaction.CommitAsync(lifetime.Token).ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && work.MaintenanceRevocation.IsCancellationRequested)
        {
            return Deferred();
        }
        catch (SqliteException exception)
        {
            return exception.SqliteErrorCode is 13 or 5 or 6
                ? new Error(ErrorCodes.Guild.StoragePressure, "Guild storage could not admit the callback.")
                : Unavailable();
        }
    }

    internal static async Task<Result<GuildRemoteCallbackBinding?>> ReadRemoteCallbackWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, string configId, string? presented, CancellationToken ct)
    {
        await using SqliteCommand query = connection.CreateCommand();

        query.Transaction = transaction;

        // The unique config-id index locates at most one receipt. LEFT joins preserve the distinction
        // between an unknown id and a known Guild binding whose authoritative source is unavailable.
        query.CommandText = """
            SELECT CASE WHEN typeof(e.QuestId)='text' AND length(e.QuestId)=36 THEN e.QuestId END,
                CASE WHEN typeof(q.RootQuestId)='text' AND length(q.RootQuestId)=36 THEN q.RootQuestId END,
                CASE WHEN typeof(e.DispatchId)='text' AND length(e.DispatchId)=36 THEN e.DispatchId END,
                CASE WHEN typeof(e.CallbackTokenHash)='text' AND length(e.CallbackTokenHash)=64
                    AND e.CallbackTokenHash NOT GLOB '*[^0-9A-F]*' THEN e.CallbackTokenHash END,
                CASE WHEN d.QuestId=e.QuestId AND d.LocalChildId IS NULL AND d.EvidenceCode=3
                    AND typeof(d.RemoteTaskId)='text' AND length(CAST(d.RemoteTaskId AS BLOB)) BETWEEN 1 AND 65536
                    AND (d.RemoteContextId IS NULL OR (typeof(d.RemoteContextId)='text'
                        AND length(CAST(d.RemoteContextId AS BLOB)) BETWEEN 1 AND 65536))
                    AND r.QuestId=e.QuestId AND b.QuestId=e.QuestId AND b.DispatchId=source.DispatchId
                    AND original.QuestId=e.QuestId AND original.EvidenceCode=3 AND original.RemoteTaskId=d.RemoteTaskId
                    AND (original.RemoteContextId IS NULL OR original.RemoteContextId=d.RemoteContextId)
                    AND b.AccountingAssociationId=q.RunId AND b.AccountId=q.AllocationAccountId
                    AND b.KindCode=$external AND b.SourceId=q.Id THEN 1 ELSE 0 END
            FROM guild_remote_callbacks e
            LEFT JOIN guild_quests q ON q.Id=e.QuestId
            LEFT JOIN guild_dispatches d ON d.DispatchId=e.DispatchId
            LEFT JOIN guild_remote_executions r ON r.QuestId=e.QuestId
            LEFT JOIN guild_remote_effects source ON source.QuestId=e.QuestId
            LEFT JOIN guild_dispatches original ON original.DispatchId=source.DispatchId
            LEFT JOIN guild_budget_effects b ON b.EffectId=source.EffectId
            WHERE e.CallbackConfigId=$config
            """;

        query.Parameters.AddWithValue("$config", configId);

        query.Parameters.AddWithValue("$external", (int)GuildBudgetEffectKind.External);

        await using SqliteDataReader reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildRemoteCallbackBinding?>.Success(null);

        Error unavailable = new(ErrorCodes.Guild.Unavailable, "The callback binding is unavailable.");

        if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3)
            || !Guid.TryParseExact(reader.GetString(0), "D", out Guid quest) || quest == Guid.Empty
            || !Guid.TryParseExact(reader.GetString(1), "D", out Guid root) || root == Guid.Empty
            || !Guid.TryParseExact(reader.GetString(2), "D", out Guid dispatch) || dispatch == Guid.Empty
            || !reader.GetBoolean(4)) return unavailable;

        string hash = reader.GetString(3);

        if (!A2ACallbackToken.Matches(presented, hash.ToLowerInvariant()))
            return new Error(ErrorCodes.Guild.Forbidden, "The callback is unavailable.");

        GuildRemoteCallbackBinding binding = new(quest, root, dispatch, hash);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? unavailable : binding;
    }
}
