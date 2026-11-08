using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Public execution plan and outputs at an admitted question; never provider reasoning.</summary>
internal sealed record GuildLocalWaitSnapshot(int Version, string InputDigest, int CurrentStep,
    ImmutableArray<PlanStep> Plan, ApprenticeCheckpoint Checkpoint);

internal sealed record GuildLocalWaitCheckpointWrite(Guid DispatchId, GuildLocalWaitSnapshot Snapshot,
    ArtifactSensitivityLabel? SourceLabel = null);

internal sealed partial class GuildQuestStore
{
    internal const string WaitCheckpointProof = """
        EXISTS(SELECT 1 FROM guild_local_executions binding JOIN guild_quests quest ON quest.Id=binding.QuestId
            JOIN guild_dispatches dispatch ON dispatch.QuestId=quest.Id AND dispatch.DispatchId=w.DispatchId
            WHERE binding.QuestId=w.QuestId
              AND (EXISTS(SELECT 1 FROM guild_content content WHERE content.ContentId=w.ContentId
                  AND content.ContentKindCode=10 AND content.SourceRecordId=w.QuestionId AND content.Revision=1
                  AND content.Digest=w.Digest AND content.MimeType='application/json' AND length(content.Body)=w.ByteLength
                  AND content.SessionId IS quest.SessionId AND content.CampaignId IS quest.CampaignId)
                OR EXISTS(SELECT 1 FROM guild_content_erasure_receipts content WHERE content.ContentId=w.ContentId
                  AND content.ContentKindCode=10 AND content.SourceRecordId=w.QuestionId AND content.Revision=1
                  AND content.Digest=w.Digest AND content.MimeType='application/json' AND content.ByteLength=w.ByteLength
                  AND content.SessionId IS quest.SessionId AND content.CampaignId IS quest.CampaignId)))
        """;

    internal Task<Result<GuildQuestStored>> WaitForInputAsync(GuildQuestDriver driver,
        Guid questionId, Guid continuationId, GuildLocalWaitCheckpointWrite checkpoint, CancellationToken ct)
        => checkpoint is null ? Task.FromResult(Result<GuildQuestStored>.Failure(InvalidReceipt))
            : WaitForInputCoreAsync(driver, questionId, continuationId, checkpoint, ct);

    internal Task<Result<GuildLocalWaitSnapshot>> ReadWaitCheckpointAsync(GuildLocalInferenceExecution execution,
        Guid continuationId, CancellationToken ct)
    {
        if (execution is null || !ReferenceEquals(execution.Store, this) || !DriverShape(execution.Driver)
            || continuationId == Guid.Empty || !execution.Dispatch.IsContinuation || execution.Dispatch.DispatchId != continuationId)
            return Task.FromResult(Result<GuildLocalWaitSnapshot>.Failure(InvalidReceipt));

        return authority.InQuestTransactionAsync<GuildLocalWaitSnapshot>(execution.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                GuildQuestDriver driver = execution.Driver;

                var admitted = await RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                var dispatch = await ReadDispatchWithinAsync(connection, transaction, continuationId, token).ConfigureAwait(false);

                if (dispatch.IsFailure) return dispatch.Error;

                if (dispatch.Value is null || !SameProviderDispatch(dispatch.Value, execution.Dispatch)
                    || dispatch.Value.Evidence != GuildDispatchEvidence.Prepared || dispatch.Value.QuestId != driver.Execution.QuestId) return Conflict;

                var input = await GuildLocalExecutionStore.ReadInputWithinAsync(connection, transaction, driver.Execution.QuestId, null, token).ConfigureAwait(false);

                if (input.IsFailure) return input.Error;

                var binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                if (binding.IsFailure) return binding.Error;

                Result frontier = await RequireWaitFrontierWithinAsync(connection, transaction, driver.Execution.QuestId, token).ConfigureAwait(false);

                if (frontier.IsFailure) return frontier.Error;

                await using SqliteCommand read = Command(connection, transaction, $"""
                    SELECT w.ContentId,w.Digest,w.ByteLength FROM guild_local_wait_checkpoints w
                    JOIN guild_continuations answer ON answer.QuestId=w.QuestId AND answer.ContinuationId=w.ContinuationId
                    WHERE w.QuestId=$quest AND w.ContinuationId=$continuation AND {WaitCheckpointProof}
                      AND answer.AcceptedRevision=(SELECT max(latest.AcceptedRevision) FROM guild_continuations latest WHERE latest.QuestId=w.QuestId)
                    """);

                Add(read, "$quest", Id(driver.Execution.QuestId));

                Add(read, "$continuation", Id(continuationId));

                GuildDataRef reference;

                await using (SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Unavailable;

                    reference = new(Guid.Parse(reader.GetString(0)), GuildContentKind.ExecutionCheckpoint, 1,
                        Convert.ToHexString((byte[])reader.GetValue(1)), "application/json", reader.GetInt64(2), false);
                }

                var content = await ReadProviderContentWithinAsync(connection, transaction, reference, token).ConfigureAwait(false);

                if (content.IsFailure) return content.Error;

                try
                {
                    GuildLocalWaitSnapshot? snapshot = JsonSerializer.Deserialize(content.Value, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot);

                    if (!ValidWaitSnapshot(snapshot, binding.Value) || JsonSerializer.Serialize(snapshot, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot) != content.Value)
                        return InvalidReceipt;

                    return snapshot!;
                }
                catch (JsonException)
                {
                    return InvalidReceipt;
                }
            }, ct);
    }

    private static bool ValidWaitSnapshot(GuildLocalWaitSnapshot? snapshot, GuildLocalExecutionBinding binding) =>
        snapshot is { Version: 1, Checkpoint: not null } && snapshot.InputDigest == binding.Input.Digest
        && !snapshot.Plan.IsDefaultOrEmpty && snapshot.CurrentStep >= 0 && snapshot.CurrentStep < snapshot.Plan.Length
        && snapshot.Checkpoint.CurrentStep == snapshot.CurrentStep && snapshot.Checkpoint.ParentApprenticeId == binding.ParentApprenticeId
        && snapshot.Checkpoint.LaunchRequested && !string.IsNullOrWhiteSpace(snapshot.Checkpoint.EscalationReason)
        && snapshot.Plan.Select((step, index) => step is not null && step.Index >= 0 && step.Description is not null
            && step.Status is "pending" or "completed" or "failed" && step.Attempts >= 0).All(valid => valid);

    private static async Task<Result> RequireWaitFrontierWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_dispatches WHERE QuestId=$quest AND EvidenceCode=2)
                OR EXISTS(SELECT 1 FROM guild_budget_effects effect WHERE effect.QuestId=$quest
                    AND (effect.StateCode IN(0,1)
                        OR NOT EXISTS(SELECT 1 FROM guild_budget_evidence receipt
                            WHERE receipt.EffectId=effect.EffectId AND receipt.AppliedRevision=effect.Revision)
                        OR (effect.ReservationId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM BudgetReservations reservation
                            WHERE lower(replace(reservation.Id,'-',''))=lower(replace(effect.ReservationId,'-',''))
                                AND reservation.Status IN(1,2)))))
                OR EXISTS(SELECT 1 FROM BudgetReservations reservation JOIN guild_quests quest
                    ON lower(replace(reservation.RunId,'-',''))=lower(replace(quest.RunId,'-',''))
                    WHERE quest.Id=$quest AND reservation.Status NOT IN(1,2))
            """);

        Add(command, "$quest", Id(questId));

        return Equals(0L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Result.Failure(UncertainDispatch);
    }

    private static async Task<Result> VerifyWaitReplayWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored quest, Guid questionId, Guid continuationId, GuildLocalWaitCheckpointWrite checkpoint, CancellationToken ct)
    {
        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(checkpoint.Snapshot, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot));

        await using SqliteCommand replay = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_local_wait_checkpoints w JOIN guild_quests q ON q.Id=w.QuestId
                WHERE w.QuestId=$quest AND w.QuestionId=$question AND w.ContinuationId=$continuation
                    AND w.DispatchId=$dispatch AND w.Digest=$digest AND q.QuestionId=w.QuestionId AND q.ContinuationId=w.ContinuationId)
            """);

        Add(replay, "$quest", Id(quest.Quest.Id));

        Add(replay, "$question", Id(questionId));

        Add(replay, "$continuation", Id(continuationId));

        Add(replay, "$dispatch", Id(checkpoint.DispatchId));

        Add(replay, "$digest", digest);

        return Equals(1L, await replay.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Result.Failure(Conflict);
    }

    private static async Task<Result> WriteWaitCheckpointWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored quest, Guid questionId, Guid continuationId, GuildLocalWaitCheckpointWrite checkpoint, DateTimeOffset now, CancellationToken ct)
    {
        var binding = await GuildLocalExecutionStore.ReadWithinAsync(connection, transaction, quest.Quest.Id, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        if (!ValidWaitSnapshot(checkpoint.Snapshot, binding.Value) || checkpoint.DispatchId == Guid.Empty) return InvalidReceipt;

        var dispatch = await ReadDispatchWithinAsync(connection, transaction, checkpoint.DispatchId, ct).ConfigureAwait(false);

        if (dispatch.IsFailure) return dispatch.Error;

        if (dispatch.Value is null || dispatch.Value.QuestId != quest.Quest.Id) return Conflict;

        Result frontier = await RequireWaitFrontierWithinAsync(connection, transaction, quest.Quest.Id, ct).ConfigureAwait(false);

        if (frontier.IsFailure) return frontier.Error;

        var inputLabel = await GuildLocalExecutionStore.ValidateReferenceAsync(connection, transaction, binding.Value.Input, ct).ConfigureAwait(false);

        if (inputLabel.IsFailure) return inputLabel.Error;

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(checkpoint.Snapshot, GuildStorageJsonContext.Default.GuildLocalWaitSnapshot);

        if (body.Length > GuildStore.MaximumContentBytes) return InvalidReceipt;

        ArtifactSensitivityLabel? source = checkpoint.SourceLabel;

        Guid contentId = Guid.NewGuid();

        DerivedArtifactWrite label = new(SensitiveArtifactKind.GuildContent, contentId, quest.Quest.SessionId,
            quest.Quest.CampaignId, source?.TurnId, 1, new CovenantDigest(SHA256.HashData(body)),
            (inputLabel.Value?.Sensitivity ?? ContentSensitivity.None) | (source?.Sensitivity ?? ContentSensitivity.None),
            (inputLabel.Value?.Provenance ?? GenerationProvenance.CreateExact([])).Merge(source?.Provenance ?? GenerationProvenance.CreateExact([])),
            source?.ProducingPlanDigest, source?.ProducingAdmissionDigest, source?.ProducingMaintenanceReceiptDigest);

        var written = await GuildStore.WriteContentWithinAsync(connection, transaction, new(contentId, questionId,
            GuildContentKind.ExecutionCheckpoint, 1, quest.Quest.SessionId, quest.Quest.CampaignId, "application/json", body, label), now, ct).ConfigureAwait(false);

        if (written.IsFailure) return written.Error;

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_local_wait_checkpoints(QuestId,QuestionId,ContinuationId,DispatchId,ContentId,Digest,ByteLength)
            VALUES($quest,$question,$continuation,$dispatch,$content,$digest,$length)
            """);

        Add(insert, "$quest", Id(quest.Quest.Id));

        Add(insert, "$question", Id(questionId));

        Add(insert, "$continuation", Id(continuationId));

        Add(insert, "$dispatch", Id(checkpoint.DispatchId));

        Add(insert, "$content", Id(contentId));

        Add(insert, "$digest", SHA256.HashData(body));

        Add(insert, "$length", body.Length);

        return await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Result.Failure(Conflict);
    }
}
