using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data.Covenant;

public sealed class CovenantV3MaintenanceCapabilityTests
{
    [Fact]
    public async Task ConsumeAsync_RejectsADifferentPurpose()
    {
        StubExclusiveLease lease = new(CovenantExclusiveOperation.CovenantReset);

        Result<CovenantV3MaintenanceCapability> minted = await CovenantV3MaintenanceCapability.MintAsync(
            lease,
            CovenantV3MaintenancePurpose.CanonicalErasure,
            CancellationToken.None);

        Assert.True(minted.IsSuccess);

        Result consumed = await minted.Value.ConsumeAsync(
            CovenantV3MaintenancePurpose.WalTruncation,
            CancellationToken.None);

        Assert.True(consumed.IsFailure);
    }

    private sealed class StubExclusiveLease(CovenantExclusiveOperation operation) : ICovenantExclusiveOperationLease
    {
        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(
            Guid.Parse("5F6E7D8C-9B0A-4132-8455-667788990011"),
            1,
            CovenantLeaseKind.Exclusive,
            CovenantLeaseCoverage.Installation,
            null,
            Guid.Parse("11111111-2222-4333-8444-555555555555"),
            1,
            1,
            0,
            null,
            null,
            null,
            null,
            new CovenantExclusiveRecoveryOwner(
                Guid.Parse("77777777-8888-4999-8AAA-BBBBBBBBBBBB"),
                operation,
                new CovenantDigest([.. Enumerable.Repeat((byte)0x44, CovenantLimits.DigestBytes)])),
            false);

        public CancellationToken Revocation => CancellationToken.None;

        public ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public Result ExecuteWhileHeld(Func<Result> callback) => callback();

        public ValueTask<Result> CompleteAsync(CovenantExclusiveLeaseDisposition disposition, CancellationToken cancellationToken) => ValueTask.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
