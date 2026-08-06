using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Kill-after-step coverage for issue #40: for every registered kind, a host that dies at each
/// durable step must converge to an explicit terminal state, and repeating recovery must not repeat
/// the handler's external work.
/// </summary>
/// <remarks>
/// "Kill" here is the honest simulation of a crash rather than a sleep: the operation is left in the
/// exact row state a dead process leaves behind — a lease that already expired, with whatever
/// checkpoint it had managed to write — and the clock is a <see cref="FakeTimeProvider"/>, so the
/// barrier is the row state itself and the test has no timing race to lose.
/// </remarks>
public sealed class LongRunningOperationCrashRecoveryTests
{
    /// <summary>Every registry kind crossed with the durable steps a crash can land between.</summary>
    public static TheoryData<string, LongRunningOperationState, bool> CrashPoints()
    {
        TheoryData<string, LongRunningOperationState, bool> data = [];

        foreach (string kind in LongRunningOperationRecoveryRegistry.KindsByStartupPriority)
        {
            foreach (LongRunningOperationState state in
                (LongRunningOperationState[])[LongRunningOperationState.Running, LongRunningOperationState.Waiting])
            {
                data.Add(kind, state, false);
                data.Add(kind, state, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task Crash_at_any_durable_step_converges_and_never_repeats_recovery(
        string kind,
        LongRunningOperationState crashedIn,
        bool hadCheckpoint)
    {
        LongRunningOperationRecoveryDescriptor descriptor =
            LongRunningOperationRecoveryRegistry.Descriptors[kind];
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(kind, descriptor.MaxCheckpointVersion);

        LongRunningOperation crashed = store.Seed(
            kind,
            descriptor.Policy,
            crashedIn,
            checkpointVersion: hadCheckpoint
                ? descriptor.MaxCheckpointVersion
                : descriptor.MinCheckpointVersion,
            leaseExpiresAt: time.GetUtcNow().AddMinutes(-5));

        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            time,
            NullLogger<LongRunningOperationReconciler>.Instance);

        _ = await reconciler.ReconcileNowAsync("restart-1");
        time.Advance(TimeSpan.FromMinutes(10));
        _ = await reconciler.ReconcileNowAsync("restart-2");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == crashed.Id);

        Assert.Single(handler.Invocations);
        Assert.Contains(
            recovered.State,
            (LongRunningOperationState[])
            [
                LongRunningOperationState.Completed,
                LongRunningOperationState.Failed,
                LongRunningOperationState.Abandoned,
            ]);
    }

    /// <summary>
    /// The same sweep with no handlers at all: a crash must still land on an explicit repair-required
    /// state naming the missing owner, never on a silent success.
    /// </summary>
    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task Crash_without_an_owning_handler_is_never_reported_as_success(
        string kind,
        LongRunningOperationState crashedIn,
        bool hadCheckpoint)
    {
        LongRunningOperationRecoveryDescriptor descriptor =
            LongRunningOperationRecoveryRegistry.Descriptors[kind];
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);

        LongRunningOperation crashed = store.Seed(
            kind,
            descriptor.Policy,
            crashedIn,
            checkpointVersion: hadCheckpoint
                ? descriptor.MaxCheckpointVersion
                : descriptor.MinCheckpointVersion,
            leaseExpiresAt: time.GetUtcNow().AddMinutes(-5));

        LongRunningOperationReconciler reconciler = new(
            store,
            [],
            time,
            NullLogger<LongRunningOperationReconciler>.Instance);

        _ = await reconciler.ReconcileNowAsync("restart-1");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == crashed.Id);

        Assert.Contains(kind, reconciler.MissingHandlerKinds);
        Assert.NotEqual(LongRunningOperationState.Completed, recovered.State);

        // AbandonSafely is the one policy where "no handler" still has a correct answer, because the
        // work is by definition not resumable. Every other kind must ask for an operator.
        if (descriptor.Policy == LongRunningOperationRecoveryPolicy.AbandonSafely)
        {
            Assert.Equal(LongRunningOperationState.Abandoned, recovered.State);
        }
        else
        {
            Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);
            Assert.Equal(
                LongRunningOperationErrorCodes.RecoveryHandlerMissing,
                recovered.TerminalErrorCode);
        }
    }

    /// <summary>
    /// A crash that leaves a checkpoint no build understands must not be retried forever, and must
    /// not be handed to the handler. It converges on an actionable repair state instead.
    /// </summary>
    [Fact]
    public async Task Crash_with_an_unreadable_checkpoint_converges_on_repair_required()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        RecordingRecoveryHandler handler = new(LongRunningOperationKinds.Apprentice, 1);

        LongRunningOperation crashed = store.Seed(
            LongRunningOperationKinds.Apprentice,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            checkpointVersion: 99,
            leaseExpiresAt: time.GetUtcNow().AddMinutes(-5));

        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            time,
            NullLogger<LongRunningOperationReconciler>.Instance);

        _ = await reconciler.ReconcileNowAsync("restart-1");
        time.Advance(TimeSpan.FromHours(1));
        _ = await reconciler.ReconcileNowAsync("restart-2");

        LongRunningOperation recovered = Assert.Single(
            store.Operations,
            operation => operation.Id == crashed.Id);

        Assert.Empty(handler.Invocations);
        Assert.Equal(LongRunningOperationState.ReconciliationRequired, recovered.State);
        Assert.Equal(
            LongRunningOperationErrorCodes.UnsupportedCheckpointVersion,
            recovered.TerminalErrorCode);
    }
}
