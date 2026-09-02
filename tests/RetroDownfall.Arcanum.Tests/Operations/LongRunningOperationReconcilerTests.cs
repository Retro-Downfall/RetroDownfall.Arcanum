using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Issue #40: recovery must be explicit. A checkpoint the handler cannot read, a kind nobody owns,
/// and a restore that has to precede ordinary writes all have to be visible rather than guessed.
/// </summary>
public sealed class LongRunningOperationReconcilerTests
{
    private static LongRunningOperationReconciler CreateReconciler(
        FakeLongRunningOperationStore store,
        TimeProvider timeProvider,
        params ILongRunningOperationRecoveryHandler[] handlers) =>
        new(
            store,
            handlers,
            timeProvider,
            NullLogger<LongRunningOperationReconciler>.Instance);

    /// <summary>
    /// DESIGN §10.8.2: the reconciler acquires a <em>fresh</em> two-minute recovery lease. Stamping every
    /// operation in a pass from the timestamp the caller captured before the pass began means that once
    /// the pass outruns the lease — trivially reached by a real recovery handler such as the attachment
    /// promotion sweep — every later lease is written already expired. `FindExpiredAsync` then matches
    /// those rows immediately, so `DurableOperationDiagnostics` counts operations being actively and
    /// correctly recovered as "expired leases nobody claimed" and `GET /api/health` degrades, with no
    /// second actor involved; an overlapping manual `POST /api/operations/reconcile` can also steal the
    /// row outright and duplicate the handler's work.
    /// </summary>
    [Fact]
    public async Task Each_recovery_lease_is_stamped_from_the_clock_at_acquisition_not_at_pass_start()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);

        // Stands in for a handler whose work outruns the two-minute recovery lease.
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.Batch,
            supportedCheckpointVersion: 0,
            _ =>
            {
                time.Advance(TimeSpan.FromMinutes(5));

                return LongRunningOperationRecoveryResult.Completed();
            });

        _ = store.Seed(LongRunningOperationKinds.Batch, LongRunningOperationRecoveryPolicy.RestartIdempotently);

        _ = store.Seed(LongRunningOperationKinds.Batch, LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, handler);

        _ = await reconciler.ReconcileNowAsync("test-owner");

        Assert.Equal(2, store.LeaseAcquisitions.Count);

        Assert.All(
            store.LeaseAcquisitions,
            acquisition =>
            {

                Assert.Equal(acquisition.ObservedNow, acquisition.SuppliedUtcNow);

                Assert.True(
                    acquisition.SuppliedExpiresAt > acquisition.ObservedNow,
                    $"Lease stamped {acquisition.SuppliedExpiresAt:O} was already expired at {acquisition.ObservedNow:O}.");

            });
    }

    /// <summary>
    /// An A2A Sending row exists at checkpoint version 0 between being registered and being
    /// checkpointed, so a kill in that window leaves a genuine row below the ledger's format
    /// version. It must reach the handler, whose own "no readable record" path abandons it with a
    /// named a2a.* reason — not be rejected on the version window and stranded as
    /// `checkpoint_version_unsupported`, which no operator repair action can clear.
    /// </summary>
    [Fact]
    public async Task An_a2a_row_that_died_before_its_first_checkpoint_is_abandoned_by_name()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.A2AInboundSending,
            supportedCheckpointVersion: 1,
            static _ => LongRunningOperationRecoveryResult.Abandoned("a2a.inbound_apprentice_missing"));
        LongRunningOperation stale = store.Seed(
            LongRunningOperationKinds.A2AInboundSending,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            checkpointVersion: 0);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, handler);
        LongRunningOperationReconciliationSummary summary =
            await reconciler.ReconcileNowAsync("test-owner");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == stale.Id);

        Assert.Equal([stale.Id], handler.Invocations);
        Assert.Equal(0, summary.RequiresAttention);
        Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);
        Assert.Equal("a2a.inbound_apprentice_missing", recovered.TerminalErrorCode);
    }

    [Fact]
    public async Task Checkpoint_above_the_handler_maximum_requires_operator_repair()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.Batch,
            supportedCheckpointVersion: 1);
        LongRunningOperation future = store.Seed(
            LongRunningOperationKinds.Batch,
            LongRunningOperationRecoveryPolicy.RestartIdempotently,
            checkpointVersion: 9);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, handler);
        _ = await reconciler.ReconcileNowAsync("test-owner");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == future.Id);

        Assert.Empty(handler.Invocations);
        Assert.Equal(
            LongRunningOperationErrorCodes.UnsupportedCheckpointVersion,
            recovered.TerminalErrorCode);
    }

    /// <summary>
    /// Requirement 9 / acceptance: restore-class recovery has to reach the state root before any
    /// ordinary durable workload writes to it.
    /// </summary>
    [Fact]
    public async Task Before_state_write_kinds_recover_ahead_of_ordinary_kinds()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        List<string> order = [];

        RecordingRecoveryHandler ordinary = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0,
            _ =>
            {
                lock (order)
                {
                    order.Add(LongRunningOperationKinds.WorkspaceIndex);
                }

                return LongRunningOperationRecoveryResult.Completed();
            });
        RecordingRecoveryHandler beforeWrites = new(
            LongRunningOperationKinds.BackupCreate,
            supportedCheckpointVersion: 0,
            _ =>
            {
                lock (order)
                {
                    order.Add(LongRunningOperationKinds.BackupCreate);
                }

                return LongRunningOperationRecoveryResult.Abandoned();
            });

        // Seeded oldest-first in the *wrong* order, so only priority can produce the right one.
        _ = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);
        time.Advance(TimeSpan.FromMinutes(1));
        _ = store.Seed(
            LongRunningOperationKinds.BackupCreate,
            LongRunningOperationRecoveryPolicy.AbandonSafely);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, ordinary, beforeWrites);
        _ = await reconciler.ReconcileAsync(
            time.GetUtcNow(),
            "test-owner",
            maxOperations: 100,
            maxConcurrency: 1);

        Assert.Equal(
            [LongRunningOperationKinds.BackupCreate, LongRunningOperationKinds.WorkspaceIndex],
            order);
    }

    /// <summary>
    /// A kind with no owning handler is a registration bug. It must be nameable by an operator, not
    /// discovered only when an operation strands.
    /// </summary>
    [Fact]
    public void Kinds_without_a_registered_handler_are_reported()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler only = new(LongRunningOperationKinds.Batch, supportedCheckpointVersion: 0);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, only);

        Assert.DoesNotContain(LongRunningOperationKinds.Batch, reconciler.MissingHandlerKinds);
        Assert.Contains(LongRunningOperationKinds.InferenceRun, reconciler.MissingHandlerKinds);
    }

    /// <summary>
    /// BackupCreate is AbandonSafely, so a registration regression that drops its handler out of
    /// a resolved scope must still be visible in the ledger. Before this fix the row closed as
    /// Abandoned with a null TerminalErrorCode and no log line — indistinguishable from a successful
    /// recovery; the non-AbandonSafely arm two lines below already names the same condition.
    /// </summary>
    [Fact]
    public async Task AbandonSafely_kind_with_no_handler_is_abandoned_with_a_named_error_code()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.BackupCreate,
            LongRunningOperationRecoveryPolicy.AbandonSafely);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time);
        _ = await reconciler.ReconcileNowAsync("test-owner");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == seeded.Id);

        Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);
        Assert.Equal(LongRunningOperationErrorCodes.RecoveryHandlerMissing, recovered.TerminalErrorCode);
    }

    /// <summary>
    /// Acceptance: repeated reconciliation must not repeat external work. A terminal operation is
    /// no longer expired, so a second pass must not reach the handler again.
    /// </summary>
    [Fact]
    public async Task Repeated_reconciliation_does_not_reinvoke_a_settled_operation()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0);
        _ = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, handler);
        _ = await reconciler.ReconcileNowAsync("first-pass");
        _ = await reconciler.ReconcileNowAsync("second-pass");

        Assert.Single(handler.Invocations);
    }

    /// <summary>
    /// "Do not hide manual-reconciliation states behind generic success": a handler that returns a
    /// non-terminal state is a bug in the handler, and must surface as repair-required.
    /// </summary>
    [Fact]
    public async Task Non_terminal_handler_result_becomes_repair_required()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0,
            static _ => new LongRunningOperationRecoveryResult(LongRunningOperationState.Running));
        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.WorkspaceIndex,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationReconciler reconciler = CreateReconciler(store, time, handler);
        _ = await reconciler.ReconcileNowAsync("test-owner");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == seeded.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);
        Assert.Equal(
            LongRunningOperationErrorCodes.InvalidRecoveryResult,
            recovered.TerminalErrorCode);
    }

    /// <summary>
    /// The reconciler and its store are both scoped, so one scope means one DbContext and one
    /// SqliteConnection — whose live-command list is not synchronized. Every concurrently recovered
    /// operation therefore has to run in its own scope; the outer scope may only page the expiry
    /// query. The sentinel store here fails the test loudly if any per-operation call is ever issued
    /// against the shared instance.
    /// </summary>
    [Fact]
    public async Task Each_concurrently_recovered_operation_runs_in_its_own_scope()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore shared = new(time);

        for (int index = 0; index < 8; index++)
        {
            _ = shared.Seed(
                LongRunningOperationKinds.WorkspaceIndex,
                LongRunningOperationRecoveryPolicy.RestartIdempotently);
        }

        RecordingRecoveryHandler scopedHandler = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0);
        RecordingRecoveryHandler sharedHandler = new(
            LongRunningOperationKinds.WorkspaceIndex,
            supportedCheckpointVersion: 0,
            static _ => throw new InvalidOperationException(
                "Recovery used the shared scope's handler."));
        RecordingServiceScopeFactory scopes = new(shared, scopedHandler);

        LongRunningOperationReconciler reconciler = new(
            new PagingOnlyOperationStore(shared),
            [sharedHandler],
            time,
            NullLogger<LongRunningOperationReconciler>.Instance,
            scopes);

        LongRunningOperationReconciliationSummary summary = await reconciler.ReconcileAsync(
            time.GetUtcNow(),
            "test-owner",
            maxOperations: 100,
            maxConcurrency: 4);

        Assert.Equal(8, summary.Claimed);
        Assert.Equal(8, scopedHandler.Invocations.Count);
        Assert.Empty(sharedHandler.Invocations);
        Assert.Equal(8, scopes.Created);
        Assert.Equal(8, scopes.Disposed);
    }

    /// <summary>
    /// Once a handler has returned an outcome, persisting it is compensation — the pass token
    /// is often the very reason compensation is running (a startup budget expiring, a background pass
    /// whose host is shutting down), so recording the result must not be lost because that same token
    /// is now cancelled. A handler that cancels the pass token immediately before returning
    /// <c>Completed</c> stands in for the startup budget expiring at the exact moment recovery
    /// finishes. The pass itself is still allowed to surface the cancellation to its caller — the
    /// startup host already has a deferred-recovery branch for that — but the outcome the handler
    /// already produced must be durable by the time it does.
    /// </summary>
    [Fact]
    public async Task Outcome_is_persisted_even_when_the_handler_cancels_the_pass_token()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        CancellationTokenSource cts = new();

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.Batch,
            supportedCheckpointVersion: 0,
            _ =>
            {
                cts.Cancel();

                return LongRunningOperationRecoveryResult.Completed();
            });

        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.Batch,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationReconciler reconciler = new(
            new CancelsOnCompensationOperationStore(store),
            [handler],
            time,
            NullLogger<LongRunningOperationReconciler>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reconciler.ReconcileAsync(
                time.GetUtcNow(),
                "test-owner",
                maxOperations: 100,
                maxConcurrency: 1,
                cts.Token));

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == seeded.Id);

        Assert.Equal(LongRunningOperationState.Completed, recovered.State);
    }

    /// <summary>
    /// The registry exists so an operator can learn what recovery does without reading the
    /// handler's source (registry header comment). <see cref="WorkspaceIndexRecoveryHandler"/> closes
    /// the row and re-enumerates nothing, deferring to the next background tick — the descriptor's
    /// operator-facing text has to say that, not its opposite, and has to name the service that
    /// actually owns the kind (<c>WorkspaceIndexingService</c>, not <c>WorkspaceIndexService</c>).
    /// <see cref="LongRunningOperationReconciler"/> is what consumes this registry at startup priority
    /// and checkpoint-window resolution, so its descriptor content is exercised here.
    /// </summary>
    [Fact]
    public void WorkspaceIndex_recovery_intent_matches_the_handler_it_describes()
    {
        LongRunningOperationRecoveryDescriptor descriptor =
            LongRunningOperationRecoveryRegistry.Find(LongRunningOperationKinds.WorkspaceIndex)!;

        Assert.Contains("close", descriptor.RecoveryIntent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("background tick", descriptor.RecoveryIntent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("re-enumerate", descriptor.RecoveryIntent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("WorkspaceIndexingService", descriptor.Owner);
    }
}

/// <summary>
/// Wraps <see cref="FakeLongRunningOperationStore"/> so <c>GetAsync</c> and <c>TryTransitionAsync</c> —
/// the two calls a reconciled outcome is persisted through — observe cancellation the way the real
/// SQLite-backed store does: both
/// open their connection on the supplied token (LongRunningOperationStore.cs), so a caller whose token
/// is already cancelled faults before either reaches the database. Every other member is outside
/// <see cref="LongRunningOperationReconciler.ReconcileAsync"/>'s call shape for a single-page,
/// unscoped pass and throws if that shape ever changes to reach it.
/// </summary>
internal sealed class CancelsOnCompensationOperationStore(FakeLongRunningOperationStore inner)
    : ILongRunningOperationStore
{
    public Task<LongRunningOperation> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<LongRunningOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return inner.GetAsync(operationId, cancellationToken);
    }

    public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
        Guid requestedOperationId,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
        LongRunningOperationQuery query,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.FindExpiredAsync(utcNow, limit, cancellationToken);

    public Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        inner.TryAcquireLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return inner.TryTransitionAsync(
            operationId,
            expectedRevision,
            ownerId,
            state,
            utcNow,
            terminalErrorCode,
            cancellationToken);
    }

    public Task<bool> RequestCancellationAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<bool> ResetForRetryAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
        CancellationToken cancellationToken = default) =>
        throw NotUsedByReconcile();

    private static InvalidOperationException NotUsedByReconcile() =>
        new("Not reached by LongRunningOperationReconciler.ReconcileAsync for a single-page, unscoped pass.");
}
