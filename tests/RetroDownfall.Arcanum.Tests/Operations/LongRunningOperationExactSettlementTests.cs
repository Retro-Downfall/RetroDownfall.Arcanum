using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Operations;

using RetroDownfall.Arcanum.Infrastructure.Operations;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// Settling one named operation through the periodic pass's own protocol.
/// </summary>
/// <remarks>
/// Pre-readiness offline-transition recovery knows which operation the authenticated journal names
/// and has already adopted its lease under the held installation maintenance lock, so it cannot use
/// the discovery half of the generic pass. What it must use is everything after that half — run the
/// handler, reread, compare-exchange the verdict — because a second copy of those three steps would
/// be a second answer to what a recovery outcome means.
/// </remarks>
public sealed class LongRunningOperationExactSettlementTests
{

    [Theory]

    [InlineData(LongRunningOperationState.Completed, LongRunningOperationSettlementOutcome.Completed)]

    [InlineData(LongRunningOperationState.Failed, LongRunningOperationSettlementOutcome.Failed)]

    [InlineData(LongRunningOperationState.Abandoned, LongRunningOperationSettlementOutcome.Abandoned)]

    [InlineData(
        LongRunningOperationState.ReconciliationRequired,
        LongRunningOperationSettlementOutcome.RequiresAttention)]
    public async Task Each_durable_verdict_is_dispatched_recorded_and_reported(
        LongRunningOperationState verdict,
        LongRunningOperationSettlementOutcome expected)
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            supportedCheckpointVersion: 0,
            _ => Verdict(verdict));

        LongRunningOperation seeded = Adopted(store);

        LongRunningOperationSettlementOutcome outcome = await Reconciler(store, time, handler)
            .SettleExactlyAsync(seeded.Id, "recovery-owner");

        Assert.Equal(expected, outcome);

        Assert.Equal(seeded.Id, Assert.Single(handler.Invocations));

        Assert.Equal(verdict, Assert.Single(store.Operations, row => row.Id == seeded.Id).State);

    }

    /// <summary>
    /// The lease is not touched here, and that is the difference from the generic pass.
    /// </summary>
    /// <remarks>
    /// The caller adopted it already, under evidence the pass does not have. A settle that took a
    /// second lease would advance the row a second time and, worse, would take it through the ordinary
    /// expiry predicate — which refuses exactly the unexpired lease a crashed process leaves behind.
    /// </remarks>
    [Fact]
    public async Task The_settle_takes_no_lease_of_its_own()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        LongRunningOperation seeded = Adopted(store);

        _ = await Reconciler(
                store,
                time,
                new RecordingRecoveryHandler(
                    LongRunningOperationKinds.DataRetentionMutation,
                    supportedCheckpointVersion: 0,
                    _ => Verdict(LongRunningOperationState.Completed)))
            .SettleExactlyAsync(seeded.Id, "recovery-owner");

        Assert.Empty(store.LeaseAcquisitions);

    }

    [Fact]
    public async Task An_operation_this_process_is_already_running_is_skipped()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        LongRunningOperationOwnership ownership = new();

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            supportedCheckpointVersion: 0,
            _ => Verdict(LongRunningOperationState.Completed));

        LongRunningOperation seeded = Adopted(store);

        Assert.True(ownership.TryClaim(seeded.Id, out _));

        LongRunningOperationSettlementOutcome outcome =
            await Reconciler(store, time, ownership, handler)
                .SettleExactlyAsync(seeded.Id, "recovery-owner");

        Assert.Equal(LongRunningOperationSettlementOutcome.OwnedInProcess, outcome);

        Assert.Empty(handler.Invocations);

    }

    [Fact]
    public async Task An_absent_row_is_reported_rather_than_invented()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        LongRunningOperationSettlementOutcome outcome = await Reconciler(store, time)
            .SettleExactlyAsync(Guid.NewGuid(), "recovery-owner");

        Assert.Equal(LongRunningOperationSettlementOutcome.NotFound, outcome);

    }

    [Fact]
    public async Task A_row_that_moved_under_the_handler_reports_the_lost_exchange()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time)
        {

            TryTransitionOverride = static _ => false,

        };

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            supportedCheckpointVersion: 0,
            _ => Verdict(LongRunningOperationState.Completed));

        LongRunningOperation seeded = Adopted(store);

        LongRunningOperationSettlementOutcome outcome = await Reconciler(store, time, handler)
            .SettleExactlyAsync(seeded.Id, "recovery-owner");

        Assert.Equal(LongRunningOperationSettlementOutcome.ConcurrencyLost, outcome);

    }

    /// <summary>
    /// The generic pass still finds, leases, and settles an expired row exactly as it did.
    /// </summary>
    /// <remarks>
    /// The extraction that gave the exact entry point its body took that body out of the pass. This is
    /// the pin that says the pass kept it: one discovery, one fresh lease, one handler call, and the
    /// verdict counted in the summary the operator surfaces read.
    /// </remarks>
    [Fact]
    public async Task The_periodic_pass_still_discovers_leases_and_settles()
    {

        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.Batch,
            supportedCheckpointVersion: 0,
            _ => Verdict(LongRunningOperationState.Completed));

        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.Batch,
            LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationReconciliationSummary summary =
            await Reconciler(store, time, handler).ReconcileNowAsync("test-owner");

        Assert.Equal(1, summary.Examined);

        Assert.Equal(1, summary.Claimed);

        Assert.Equal(1, summary.Completed);

        Assert.Equal(seeded.Id, Assert.Single(handler.Invocations));

        Assert.Single(store.LeaseAcquisitions);

    }

    /// <summary>
    /// A row whose lease this process has already adopted, which is the only state this entry point
    /// is reached in.
    /// </summary>
    /// <remarks>
    /// The verdict's compare-exchange is owner-bound, so the settle has to run under the owner the
    /// adoption wrote. Seeding a row still naming the crashed process and settling as somebody else
    /// would prove only that the store refuses a stranger — which it should, and which is a different
    /// test.
    /// </remarks>
    private static LongRunningOperation Adopted(FakeLongRunningOperationStore store)
    {

        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete);

        LongRunningOperation adopted = seeded with { LeaseOwner = "recovery-owner" };

        store.Add(adopted);

        return adopted;

    }

    private static LongRunningOperationRecoveryResult Verdict(LongRunningOperationState state) =>
        state switch
        {
            LongRunningOperationState.Completed => LongRunningOperationRecoveryResult.Completed(),
            LongRunningOperationState.Failed =>
                LongRunningOperationRecoveryResult.Failed("test.failed"),
            LongRunningOperationState.Abandoned =>
                LongRunningOperationRecoveryResult.Abandoned("test.abandoned"),
            _ => LongRunningOperationRecoveryResult.RequiresAttention("test.attention"),
        };

    private static LongRunningOperationReconciler Reconciler(
        FakeLongRunningOperationStore store,
        TimeProvider timeProvider,
        params ILongRunningOperationRecoveryHandler[] handlers) =>
        Reconciler(store, timeProvider, new LongRunningOperationOwnership(), handlers);

    private static LongRunningOperationReconciler Reconciler(
        FakeLongRunningOperationStore store,
        TimeProvider timeProvider,
        LongRunningOperationOwnership ownership,
        params ILongRunningOperationRecoveryHandler[] handlers) =>
        new(
            store,
            handlers,
            timeProvider,
            NullLogger<LongRunningOperationReconciler>.Instance,
            ownership);

}
