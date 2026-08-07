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
}
