using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

public sealed class LongRunningOperationReconciler(
    ILongRunningOperationStore store,
    IEnumerable<ILongRunningOperationRecoveryHandler> handlers,
    TimeProvider timeProvider,
    ILogger<LongRunningOperationReconciler> logger)
{
    private static readonly TimeSpan RecoveryLease = TimeSpan.FromMinutes(2);

    private readonly IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> _handlers =
        handlers.ToDictionary(static handler => handler.Kind, StringComparer.Ordinal);

    public Task<LongRunningOperationReconciliationSummary> ReconcileNowAsync(
        string ownerId,
        int maxOperations = 100,
        int maxConcurrency = 4,
        CancellationToken cancellationToken = default) =>
        ReconcileAsync(
            timeProvider.GetUtcNow(),
            ownerId,
            maxOperations,
            maxConcurrency,
            cancellationToken);

    public async Task<LongRunningOperationReconciliationSummary> ReconcileAsync(
        DateTimeOffset utcNow,
        string ownerId,
        int maxOperations,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        int boundedOperations = Math.Clamp(maxOperations, 1, 1_000);
        int boundedConcurrency = Math.Clamp(maxConcurrency, 1, 16);
        IReadOnlyList<LongRunningOperation> expired = await store
            .FindExpiredAsync(utcNow, boundedOperations, cancellationToken)
            .ConfigureAwait(false);

        int claimed = 0;
        int completed = 0;
        int failed = 0;
        int abandoned = 0;
        int attention = 0;
        int skipped = 0;

        await Parallel.ForEachAsync(
            expired,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = boundedConcurrency,
            },
            async (operation, ct) =>
            {
                LongRunningOperationLeaseResult lease = await store.TryAcquireLeaseAsync(
                    operation.Id,
                    ownerId,
                    utcNow,
                    utcNow.Add(RecoveryLease),
                    ct).ConfigureAwait(false);
                if (!lease.Acquired)
                {
                    Interlocked.Increment(ref skipped);
                    RecordOutcome(operation.Kind, "lease_lost");
                    return;
                }

                Interlocked.Increment(ref claimed);
                LongRunningOperationRecoveryResult result = await RecoverOneAsync(lease.Operation, ct)
                    .ConfigureAwait(false);
                bool transitioned = await store.TryTransitionAsync(
                    lease.Operation.Id,
                    lease.Operation.Revision,
                    ownerId,
                    result.State,
                    timeProvider.GetUtcNow(),
                    result.ErrorCode,
                    ct).ConfigureAwait(false);
                if (!transitioned)
                {
                    Interlocked.Increment(ref skipped);
                    RecordOutcome(operation.Kind, "cas_lost");
                    return;
                }

                switch (result.State)
                {
                    case LongRunningOperationState.Completed:
                        Interlocked.Increment(ref completed);
                        RecordOutcome(operation.Kind, "completed");
                        break;
                    case LongRunningOperationState.Failed:
                        Interlocked.Increment(ref failed);
                        RecordOutcome(operation.Kind, "failed");
                        break;
                    case LongRunningOperationState.Abandoned:
                        Interlocked.Increment(ref abandoned);
                        RecordOutcome(operation.Kind, "abandoned");
                        break;
                    default:
                        Interlocked.Increment(ref attention);
                        RecordOutcome(operation.Kind, "attention");
                        break;
                }
            }).ConfigureAwait(false);

        return new LongRunningOperationReconciliationSummary(
            expired.Count,
            claimed,
            completed,
            failed,
            abandoned,
            attention,
            skipped);
    }

    private async Task<LongRunningOperationRecoveryResult> RecoverOneAsync(
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(operation.Kind, out ILongRunningOperationRecoveryHandler? handler))
        {
            if (operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.AbandonSafely)
            {
                return LongRunningOperationRecoveryResult.Abandoned();
            }

            logger.LogWarning(
                "No recovery handler is registered for durable operation kind {OperationKind}.",
                operation.Kind);
            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.RecoveryHandlerMissing);
        }

        if (operation.CheckpointVersion > handler.SupportedCheckpointVersion)
        {
            logger.LogWarning(
                "Operation {OperationId} checkpoint version {CheckpointVersion} exceeds supported version {SupportedVersion}.",
                operation.Id,
                operation.CheckpointVersion,
                handler.SupportedCheckpointVersion);
            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.UnsupportedCheckpointVersion);
        }

        try
        {
            LongRunningOperationRecoveryResult result = await handler
                .RecoverAsync(operation, cancellationToken)
                .ConfigureAwait(false);
            if (result.State is not (
                LongRunningOperationState.Completed
                or LongRunningOperationState.Failed
                or LongRunningOperationState.Abandoned
                or LongRunningOperationState.ReconciliationRequired))
            {
                return LongRunningOperationRecoveryResult.RequiresAttention(
                    LongRunningOperationErrorCodes.InvalidRecoveryResult);
            }

            return result;
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Operation {OperationId} has a corrupt recovery checkpoint.", operation.Id);
            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.CorruptCheckpoint);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Recovery failed for operation {OperationId}.", operation.Id);
            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.RecoveryFailed);
        }
    }

    private static void RecordOutcome(string kind, string outcome) =>
        ArcanumMetrics.OperationReconciliationTotal.Add(
            1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("outcome", outcome));
}
