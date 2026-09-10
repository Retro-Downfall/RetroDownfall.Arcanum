using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Tests.Operations;

public sealed class LongRunningOperationOwnershipTests
{
    [Fact]
    public void IsClaimedByRequiresTheExactOperationAndToken()
    {
        LongRunningOperationOwnership ownership = new();

        Guid operationId = Guid.NewGuid();

        Assert.True(ownership.TryClaim(operationId, out Guid token));

        Assert.True(ownership.IsClaimedBy(operationId, token));

        Assert.False(ownership.IsClaimedBy(operationId, Guid.NewGuid()));

        Assert.False(ownership.IsClaimedBy(Guid.NewGuid(), token));

        Assert.False(ownership.IsClaimedBy(Guid.Empty, token));

        Assert.True(ownership.Release(operationId, token));

        Assert.False(ownership.IsClaimedBy(operationId, token));
    }
}
