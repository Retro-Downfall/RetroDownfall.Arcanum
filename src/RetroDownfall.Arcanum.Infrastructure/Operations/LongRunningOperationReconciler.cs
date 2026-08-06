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

    /// <summary>
    /// Restore-class kinds own whole directories under the state root, so they must settle before an
    /// ordinary durable workload appends to a tree that is about to be rolled back or replaced (#40).
    /// </summary>
    /// <remarks>
    /// The ordering is per-page. <see cref="ILongRunningOperationStore.FindExpiredAsync"/> has no
    /// offset — it always returns the head of the expired set — so a backlog large enough to fill a
    /// page with ordinary kinds can push a pre-state-write operation past the first phase, and it is
    /// then recovered in the second. It is still recovered in the same pass, and still before the
    /// host finishes starting; only its position relative to other kinds is best-effort. Making it
    /// exact needs a kind-filtered expiry query, not a change here.
    /// </remarks>
    private static readonly LongRunningOperationStartupPriority[] StartupPhases =
    [
        LongRunningOperationStartupPriority.BeforeStateWrites,
        LongRunningOperationStartupPriority.Readiness,
    ];

    private readonly IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> _handlers =
        handlers.ToDictionary(static handler => handler.Kind, StringComparer.Ordinal);

    /// <summary>
    /// Registered kinds with no owning handler. A non-empty set is a registration bug rather than a
    /// runtime condition, so operator surfaces name it instead of waiting for an operation to strand
    /// against a missing handler (#40).
    /// </summary>
    public IReadOnlyList<string> MissingHandlerKinds { get; } = BuildMissingHandlerKinds(handlers);

    private static IReadOnlyList<string> BuildMissingHandlerKinds(
        IEnumerable<ILongRunningOperationRecoveryHandler> registered)
    {
        HashSet<string> owned =
        [
            .. registered.Select(static handler => handler.Kind),
        ];

        return
        [
            .. LongRunningOperationRecoveryRegistry.KindsByStartupPriority.Where(kind => !owned.Contains(kind)),
        ];
    }

    /// <summary>
    /// Startup phase for a kind. An unregistered kind cannot claim the pre-state-write slot, so it
    /// reconciles with ordinary work.
    /// </summary>
    private static LongRunningOperationStartupPriority PriorityOf(string kind) =>
        LongRunningOperationRecoveryRegistry.Find(kind)?.StartupPriority
        ?? LongRunningOperationStartupPriority.Readiness;

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
        int pageSize = Math.Clamp(maxOperations, 1, 1_000);

        int boundedConcurrency = Math.Clamp(maxConcurrency, 1, 16);

        int examined = 0;

        int claimed = 0;
        int completed = 0;
        int failed = 0;
        int abandoned = 0;
        int attention = 0;
        int skipped = 0;

        HashSet<Guid> attempted = [];

        foreach (LongRunningOperationStartupPriority phase in StartupPhases)
        {

            while (true)
            {

                IReadOnlyList<LongRunningOperation> expired = await store
                    .FindExpiredAsync(utcNow, pageSize, cancellationToken)
                    .ConfigureAwait(false);

                LongRunningOperation[] page = expired
                    .Where(operation => PriorityOf(operation.Kind) == phase)
                    .Where(operation => attempted.Add(operation.Id))
                    .ToArray();

                if (page.Length == 0)
                {

                    break;

                }

                examined += page.Length;

                await Parallel.ForEachAsync(
                    page,
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

                        LongRunningOperation latest = await store.GetAsync(
                            lease.Operation.Id,
                            ct).ConfigureAwait(false)
                            ?? lease.Operation;

                        bool transitioned = await store.TryTransitionAsync(
                            lease.Operation.Id,
                            latest.Revision,
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

            }

        }

        return new LongRunningOperationReconciliationSummary(
            examined,
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

        // The handler declares the newest payload it writes; the registry declares the oldest it still
        // understands. A checkpoint outside that window is unreadable in either direction, and handing
        // it to handler code risks acting on a misparsed payload rather than failing safely (#40).
        LongRunningOperationRecoveryDescriptor? descriptor =
            LongRunningOperationRecoveryRegistry.Find(operation.Kind);

        int minimumVersion = descriptor?.MinCheckpointVersion ?? 0;

        int maximumVersion = descriptor is null
            ? handler.SupportedCheckpointVersion
            : Math.Min(descriptor.MaxCheckpointVersion, handler.SupportedCheckpointVersion);

        if (operation.CheckpointVersion < minimumVersion || operation.CheckpointVersion > maximumVersion)
        {
            logger.LogWarning(
                "Operation {OperationId} checkpoint version {CheckpointVersion} is outside the supported "
                + "window [{MinimumVersion}, {MaximumVersion}] for kind {OperationKind}.",
                operation.Id,
                operation.CheckpointVersion,
                minimumVersion,
                maximumVersion,
                operation.Kind);
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
