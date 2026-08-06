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
    /// A payload written by a newer build is already rejected. A payload written by an *older*
    /// build the handler has since stopped understanding is just as unreadable, and silently
    /// handing it to the handler risks acting on misparsed recovery state. The A2A Sending kinds
    /// are the real case: their ledger format starts at version 1, so a version 0 row predates the
    /// contract entirely.
    /// </summary>
    [Fact]
    public async Task Checkpoint_below_the_registry_minimum_requires_operator_repair()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.A2AInboundSending,
            supportedCheckpointVersion: 1);
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

        Assert.Empty(handler.Invocations);
        Assert.Equal(1, summary.RequiresAttention);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);
        Assert.Equal(
            LongRunningOperationErrorCodes.UnsupportedCheckpointVersion,
            recovered.TerminalErrorCode);
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
}
