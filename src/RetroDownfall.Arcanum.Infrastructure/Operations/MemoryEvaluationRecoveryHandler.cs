using RetroDownfall.Arcanum.Core.Primitives;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>Settles retained metadata and measured bills; never reconstructs a request capability or dispatches.</summary>
internal sealed class MemoryEvaluationRecoveryHandler(
    ILongRunningOperationStore operations,
    IMemoryEvaluationEvidenceStore evidence,
    ICovenantConnectionSource connections,
    ITurnRunWriter runs,
    IBudgetReservationService reservations,
    TimeProvider timeProvider) : ILongRunningOperationRecoveryHandler
{
    public string Kind => LongRunningOperationKinds.MemoryEvaluation;

    public int SupportedCheckpointVersion => 1;

    public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
    {
        if (operation.Kind != Kind || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.AbandonSafely)
            return Corrupt();

        if (operation.CheckpointVersion is < 0 or > 1)
            return LongRunningOperationRecoveryResult.RequiresAttention(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);

        MemoryEvaluationCheckpoint? checkpoint = null;

        if (operation.CheckpointReference is not null || operation.CheckpointVersion == 0 && operation.CheckpointPayload is not null)
            return Corrupt();

        if (operation.CheckpointVersion == 1 && (checkpoint = MemoryEvaluationCheckpointCodec.Decode(operation.CheckpointPayload)) is null)
            return Corrupt();

        LongRunningOperationRequestIdentity? identity = await operations.FindRequestIdentityAsync(operation.Id, cancellationToken).ConfigureAwait(false);

        if (identity is null || checkpoint is not null && (checkpoint.EvaluationId != identity.RequestedOperationId
            || checkpoint.OperationId != operation.Id || checkpoint.RequestDigest != identity.ApplyRequestDigest || checkpoint.PlanDigest != identity.EffectDigest))
            return Corrupt();

        var read = await evidence.GetAsync(identity.RequestedOperationId, cancellationToken).ConfigureAwait(false);

        if (read.IsFailure && read.Error.Code != ErrorCodes.MemoryEvaluation.NotFound)
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.MemoryEvaluation.Unavailable);

        MemoryEvaluationStoredEvidence? retained = read.IsSuccess ? read.Value : null;

        if (retained is not null && (retained.Registration.OperationId != operation.Id
            || retained.Registration.RequestDigest != identity.ApplyRequestDigest || retained.Registration.PlanDigest != identity.EffectDigest
            || checkpoint is not null && (checkpoint.PairCount != retained.Registration.PairCount
                || checkpoint.Aggregate.Checks.Length != retained.Registration.CheckCount && !checkpoint.Aggregate.Checks.IsEmpty))) return Corrupt();

        int? pairCount = checkpoint?.PairCount ?? retained?.Registration.PairCount;

        if (pairCount is { } count)
        {
            List<Guid>? selected = await ReadArmRunIdsAsync(identity.RequestedOperationId, count, cancellationToken).ConfigureAwait(false);

            if (selected is null) return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.MemoryEvaluation.AmbiguousAccounting);

            foreach (Guid runId in selected)
            {
                _ = await runs.TryAbandonRunAsync(runId, cancellationToken).ConfigureAwait(false);

                await reservations.ReconcileRecordedUsageAsync(runId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (retained?.Report is not null) return LongRunningOperationRecoveryResult.Completed();

        if (retained is null) return LongRunningOperationRecoveryResult.Abandoned(checkpoint is null ? ErrorCodes.MemoryEvaluation.NotPrepared : ErrorCodes.MemoryEvaluation.Erased);

        MemoryEvaluationAggregate aggregate = checkpoint?.Aggregate
            ?? new(0, 0, 0, 0, 0, 0, 0, [], retained.Registration.PlanDigest);

        aggregate = aggregate with { FailureReason = MemoryEvaluationReason.Interrupted };

        DateTimeOffset now = timeProvider.GetUtcNow();

        var report = new MemoryEvaluationTerminalReport(retained.Registration,
            now < retained.Registration.CreatedAtUtc ? retained.Registration.CreatedAtUtc : now,
            MemoryEvaluationState.Interrupted, MemoryEvaluationPolicy.Classify(aggregate, retained.Registration.PairCount),
            aggregate, retained.Sources, null, null, null, checkpoint?.ElapsedMilliseconds ?? 0, retained.Registration.Dependencies);

        var completed = await evidence.CompleteAsync(identity.RequestedOperationId, report, cancellationToken).ConfigureAwait(false);

        if (completed.IsFailure && completed.Error.Code != ErrorCodes.MemoryEvaluation.NotFound)
            return LongRunningOperationRecoveryResult.RequiresAttention(ErrorCodes.MemoryEvaluation.Unavailable);

        return LongRunningOperationRecoveryResult.Abandoned(completed.IsSuccess ? ErrorCodes.MemoryEvaluation.Interrupted : ErrorCodes.MemoryEvaluation.Erased);
    }

    private static LongRunningOperationRecoveryResult Corrupt() => LongRunningOperationRecoveryResult.RequiresAttention(LongRunningOperationErrorCodes.CorruptCheckpoint);

    private async Task<List<Guid>?> ReadArmRunIdsAsync(Guid evaluationId, int pairCount, CancellationToken ct)
    {
        if (pairCount is < MemoryEvaluationLimits.MinPairs or > MemoryEvaluationLimits.MaxPairs) return null;

        SqliteConnection connection = await connections.GetOpenCoreConnectionAsync(ct).ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        string[] parameters = Enumerable.Range(0, pairCount * 2).Select(index => "$request" + index.ToString(CultureInfo.InvariantCulture)).ToArray();

        command.CommandText = "SELECT Id, RequestId FROM InferenceRuns WHERE SessionId IS NULL AND Surface=$surface AND Purpose=$purpose AND RequestId IN (" + string.Join(',', parameters) + ")";

        command.Parameters.AddWithValue("$surface", "memory-evaluation");

        command.Parameters.AddWithValue("$purpose", "counterfactual-arm");

        for (int index = 0; index < parameters.Length; index++)
            command.Parameters.AddWithValue(parameters[index], evaluationId.ToString("N") + ":" + index.ToString(CultureInfo.InvariantCulture));

        List<Guid> selected = [];

        HashSet<string> requests = new(StringComparer.Ordinal);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!requests.Add(reader.GetString(1)) || !Guid.TryParseExact(reader.GetString(0), "N", out Guid runId)) return null;

            selected.Add(runId);
        }

        return selected;
    }
}
