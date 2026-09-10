using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class LongRunningOperationExactSettlementTests
{
    [Fact]
    public async Task Public_exact_settlement_without_authenticated_owner_evidence_is_fail_closed()
    {
        FakeTimeProvider time = new();
        FakeLongRunningOperationStore store = new(time);
        LongRunningOperation seeded = store.Seed(
            LongRunningOperationKinds.DataRetentionMutation,
            LongRunningOperationRecoveryPolicy.ReconcileAndComplete,
            checkpointVersion: 4);
        RecordingRecoveryHandler handler = new(
            LongRunningOperationKinds.DataRetentionMutation,
            supportedCheckpointVersion: 4);
        LongRunningOperationReconciler reconciler = new(
            store,
            [handler],
            time,
            NullLogger<LongRunningOperationReconciler>.Instance,
            new LongRunningOperationOwnership());

        LongRunningOperationSettlementOutcome outcome = await reconciler
            .SettleExactlyAsync(seeded.Id, "recovery-owner");

        Assert.Equal(LongRunningOperationSettlementOutcome.RequiresAttention, outcome);
        Assert.Empty(handler.Invocations);
        Assert.Equal(seeded, Assert.Single(store.Operations));
    }
}
