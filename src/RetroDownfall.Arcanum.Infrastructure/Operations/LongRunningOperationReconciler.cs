using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>What settling one durable operation ended as.</summary>
/// <remarks>
/// Named rather than counted, because one caller needs the verdict for a single operation rather than
/// a tally over a page. Pre-readiness recovery may publish readiness only on the three terminal
/// answers; everything else means the operation is still owed and the host must stay closed.
/// </remarks>
public enum LongRunningOperationSettlementOutcome : byte
{

    /// <summary>This process is already running it, so nothing was done.</summary>
    OwnedInProcess = 1,

    /// <summary>No such durable row.</summary>
    NotFound = 2,

    /// <summary>The handler finished it.</summary>
    Completed = 3,

    /// <summary>The handler recorded a durable failure.</summary>
    Failed = 4,

    /// <summary>The handler abandoned it safely.</summary>
    Abandoned = 5,

    /// <summary>The handler could not finish it, and an operator has to look.</summary>
    RequiresAttention = 6,

    /// <summary>Somebody moved the row between the handler and the verdict.</summary>
    ConcurrencyLost = 7,

}

/// <param name="scopeFactory">
/// Supplies one DI scope — and therefore one <c>ArcanumDbContext</c> and one SQLite connection — per
/// concurrently recovered operation. The reconciler and its store are both scoped, so without this
/// the fan-out below would run several workers' commands over a single <c>SqliteConnection</c>,
/// which tracks its live commands in an unsynchronized list. When it is absent (direct construction
/// outside DI) recovery runs one operation at a time, because sharing one connection is the only
/// alternative and it is not safe.
/// </param>
public sealed class LongRunningOperationReconciler(
    ILongRunningOperationStore store,
    IEnumerable<ILongRunningOperationRecoveryHandler> handlers,
    TimeProvider timeProvider,
    ILogger<LongRunningOperationReconciler> logger,
    LongRunningOperationOwnership ownership,
    IServiceScopeFactory? scopeFactory = null)
{
    private static readonly TimeSpan RecoveryLease = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Restore-class kinds own whole directories under the state root, so they must settle before an
    /// ordinary durable workload appends to a tree that is about to be rolled back or replaced.
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
    /// against a missing handler.
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

        // Concurrency needs a scope per worker. Without a scope factory every worker would share one
        // DbContext connection, so the only correct fan-out is none.
        int boundedConcurrency = scopeFactory is null ? 1 : Math.Clamp(maxConcurrency, 1, 16);

        int examined = 0;

        int claimed = 0;
        int completed = 0;
        int failed = 0;
        int abandoned = 0;
        int attention = 0;
        int skipped = 0;

        HashSet<Guid> attempted = [];

        async Task SettleAsync(
            ILongRunningOperationStore operationStore,
            IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> operationHandlers,
            LongRunningOperation operation,
            CancellationToken ct)
        {

            // Read the clock here rather than reusing the pass-start `utcNow`. That timestamp is the
            // discovery predicate for the whole pass, and a pass over a real backlog easily outruns the
            // two-minute lease, so stamping from it writes leases that are already expired the moment they
            // are taken: `DurableOperationDiagnostics` then counts operations being actively and correctly
            // recovered as "expired leases nobody claimed" and degrades `GET /api/health`, and an
            // overlapping manual reconcile can steal the row and duplicate the handler's work. DESIGN
            // §10.8.2 requires a *fresh* recovery lease per operation.
            // An operation this process is already running is skipped before its lease is even looked
            // at. An offline transition stops renewing for the length of its closed period, so its row
            // looks abandoned to anything deciding by the lease alone — and starting a second recovery
            // beside a transition that is still erasing is the one outcome that cannot be undone.
            if (ownership.IsClaimed(operation.Id))
            {

                Interlocked.Increment(ref skipped);

                RecordOutcome(operation.Kind, "owned_in_process");

                return;

            }

            DateTimeOffset leaseTakenAt = timeProvider.GetUtcNow();

            LongRunningOperationLeaseResult lease = await operationStore.TryAcquireLeaseAsync(
                operation.Id,
                ownerId,
                leaseTakenAt,
                leaseTakenAt.Add(RecoveryLease),
                ct).ConfigureAwait(false);

            if (!lease.Acquired)
            {

                Interlocked.Increment(ref skipped);

                RecordOutcome(operation.Kind, "lease_lost");

                return;

            }

            Interlocked.Increment(ref claimed);

            switch (await SettleLeasedAsync(
                operationStore,
                operationHandlers,
                lease.Operation,
                ownerId,
                ct).ConfigureAwait(false))
            {
                case LongRunningOperationSettlementOutcome.Completed:
                    Interlocked.Increment(ref completed);
                    break;
                case LongRunningOperationSettlementOutcome.Failed:
                    Interlocked.Increment(ref failed);
                    break;
                case LongRunningOperationSettlementOutcome.Abandoned:
                    Interlocked.Increment(ref abandoned);
                    break;
                case LongRunningOperationSettlementOutcome.ConcurrencyLost:
                    Interlocked.Increment(ref skipped);
                    break;
                default:
                    Interlocked.Increment(ref attention);
                    break;
            }

        }

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

                        if (scopeFactory is null)
                        {

                            await SettleAsync(store, _handlers, operation, ct).ConfigureAwait(false);

                            return;

                        }

                        await using AsyncServiceScope operationScope = scopeFactory.CreateAsyncScope();

                        ILongRunningOperationStore scopedStore = operationScope.ServiceProvider
                            .GetRequiredService<ILongRunningOperationStore>();

                        Dictionary<string, ILongRunningOperationRecoveryHandler> scopedHandlers =
                            operationScope.ServiceProvider
                                .GetServices<ILongRunningOperationRecoveryHandler>()
                                .ToDictionary(static handler => handler.Kind, StringComparer.Ordinal);

                        await SettleAsync(scopedStore, scopedHandlers, operation, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Settles exactly one named operation whose lease this caller already holds.
    /// </summary>
    /// <remarks>
    /// The pass's own per-operation protocol, reached by identity instead of by expiry discovery.
    /// Pre-readiness offline-transition recovery knows which operation the authenticated journal names
    /// and has already adopted its lease under the held installation maintenance lock, so the two
    /// things the generic pass does first — find an expired row, then take its lease — are the two it
    /// must not repeat. Everything after that is identical, and is identical by being the same code:
    /// a second copy of "run the handler, reread, compare-exchange the verdict" would be a second
    /// answer to what a recovery outcome means.
    /// </remarks>
    public async Task<LongRunningOperationSettlementOutcome> SettleExactlyAsync(
        Guid operationId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        LongRunningOperation? leased = await store
            .GetAsync(operationId, cancellationToken)
            .ConfigureAwait(false);

        if (leased is null)
        {

            return LongRunningOperationSettlementOutcome.NotFound;

        }

        // The same skip the generic pass makes, for the same reason: an operation this process is
        // already running must not be recovered beside itself. It is checked after the read here only
        // so the metric can carry the row's real kind, which an identity alone does not name.
        if (ownership.IsClaimed(operationId))
        {

            RecordOutcome(leased.Kind, "owned_in_process");

            return LongRunningOperationSettlementOutcome.OwnedInProcess;

        }

        return await SettleLeasedAsync(store, _handlers, leased, ownerId, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Runs the handler for one leased operation and records the verdict it returned.
    /// </summary>
    /// <remarks>
    /// Once the handler has returned, persisting its outcome is compensation: the caller's token is
    /// often the reason compensation is running at all — a startup budget that just expired, a
    /// background pass whose host is shutting down — so recording the result must not be lost because
    /// that same token is now cancelled. The reread and the compare-exchange therefore run on
    /// <see cref="CancellationToken.None"/>, and the caller's token stays scoped to the handler call.
    /// </remarks>
    private async Task<LongRunningOperationSettlementOutcome> SettleLeasedAsync(
        ILongRunningOperationStore operationStore,
        IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> operationHandlers,
        LongRunningOperation leased,
        string ownerId,
        CancellationToken cancellationToken)
    {

        LongRunningOperationRecoveryResult result = await RecoverOneAsync(
            operationHandlers,
            leased,
            cancellationToken).ConfigureAwait(false);

        LongRunningOperation latest = await operationStore.GetAsync(
            leased.Id,
            CancellationToken.None).ConfigureAwait(false)
            ?? leased;

        bool transitioned = await operationStore.TryTransitionAsync(
            leased.Id,
            latest.Revision,
            ownerId,
            result.State,
            timeProvider.GetUtcNow(),
            result.ErrorCode,
            CancellationToken.None).ConfigureAwait(false);

        if (!transitioned)
        {

            RecordOutcome(leased.Kind, "cas_lost");

            return LongRunningOperationSettlementOutcome.ConcurrencyLost;

        }

        switch (result.State)
        {
            case LongRunningOperationState.Completed:
                RecordOutcome(leased.Kind, "completed");
                return LongRunningOperationSettlementOutcome.Completed;
            case LongRunningOperationState.Failed:
                RecordOutcome(leased.Kind, "failed");
                return LongRunningOperationSettlementOutcome.Failed;
            case LongRunningOperationState.Abandoned:
                RecordOutcome(leased.Kind, "abandoned");
                return LongRunningOperationSettlementOutcome.Abandoned;
            default:
                RecordOutcome(leased.Kind, "attention");
                return LongRunningOperationSettlementOutcome.RequiresAttention;
        }

    }

    private async Task<LongRunningOperationRecoveryResult> RecoverOneAsync(
        IReadOnlyDictionary<string, ILongRunningOperationRecoveryHandler> handlersForOperation,
        LongRunningOperation operation,
        CancellationToken cancellationToken)
    {
        if (!handlersForOperation.TryGetValue(operation.Kind, out ILongRunningOperationRecoveryHandler? handler))
        {
            logger.LogWarning(
                "No recovery handler is registered for durable operation kind {OperationKind}.",
                operation.Kind);

            if (operation.RecoveryPolicy == LongRunningOperationRecoveryPolicy.AbandonSafely)
            {
                return LongRunningOperationRecoveryResult.Abandoned(
                    LongRunningOperationErrorCodes.RecoveryHandlerMissing);
            }

            return LongRunningOperationRecoveryResult.RequiresAttention(
                LongRunningOperationErrorCodes.RecoveryHandlerMissing);
        }

        // The handler declares the newest payload it writes; the registry declares the oldest it still
        // understands. A checkpoint outside that window is unreadable in either direction, and handing
        // it to handler code risks acting on a misparsed payload rather than failing safely.
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
