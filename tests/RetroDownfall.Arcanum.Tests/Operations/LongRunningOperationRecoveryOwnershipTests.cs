using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class LongRunningOperationRecoveryOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_recovery_claims_process_ownership_before_lease_acquisition_and_releases_after_settlement(bool runtime)
    {
        FakeTimeProvider time = new();

        FakeLongRunningOperationStore store = new(time);

        LongRunningOperation row = store.Seed(LongRunningOperationKinds.BlobEncryptionMigration, LongRunningOperationRecoveryPolicy.RestartIdempotently);

        LongRunningOperationOwnership ownership = new();

        OwnershipAcquisition acquisition = new(store, ownership);

        OwnershipHandler handler = new(ownership);

        LongRunningOperationReconciler reconciler = new(store, [handler], time,
            NullLogger<LongRunningOperationReconciler>.Instance, ownership, store, acquisition, null);

        if (runtime)
            _ = await reconciler.SettleDiscoveredRuntimeAsync(row, "test-recovery", default);
        else
            _ = await reconciler.ReconcileNowAsync("test-recovery");

        Assert.True(acquisition.ClaimedBeforeLease);

        Assert.True(handler.ClaimedDuringHandler);

        Assert.False(ownership.IsClaimed(row.Id));

        Assert.Equal(LongRunningOperationState.Completed, (await store.GetAsync(row.Id))!.State);
    }

    private sealed class OwnershipAcquisition(FakeLongRunningOperationStore store, LongRunningOperationOwnership ownership)
        : ILongRunningOperationClassifiedRecoveryLeaseAcquisition
    {
        internal bool ClaimedBeforeLease;

        public Task<LongRunningOperationLeaseResult> TryAcquireClassifiedRecoveryLeaseAsync(LongRunningOperationRecoveryFingerprint expected,
            string ownerId, DateTimeOffset utcNow, DateTimeOffset leaseExpiresAt, CancellationToken cancellationToken = default)
        {
            ClaimedBeforeLease = ownership.IsClaimed(expected.OperationId);

            return store.TryAcquireClassifiedRecoveryLeaseAsync(expected, ownerId, utcNow, leaseExpiresAt, cancellationToken);
        }
    }

    private sealed class OwnershipHandler(LongRunningOperationOwnership ownership) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => LongRunningOperationKinds.BlobEncryptionMigration;

        public int SupportedCheckpointVersion => 0;

        internal bool ClaimedDuringHandler;

        public Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
        {
            ClaimedDuringHandler = ownership.IsClaimed(operation.Id);

            return Task.FromResult(LongRunningOperationRecoveryResult.Completed());
        }
    }
}
