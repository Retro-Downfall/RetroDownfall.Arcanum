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

    [Fact]
    public void Observation_does_not_follow_a_replaced_claim()
    {
        LongRunningOperationOwnership ownership = new();

        Guid operation = Guid.NewGuid();

        Assert.Null(ownership.Observe(operation));

        Assert.True(ownership.TryClaim(operation, out Guid first));

        var observed = ownership.Observe(operation);

        Assert.NotNull(observed);

        Assert.True(observed.IsCurrent);

        Assert.True(ownership.Release(operation, first));

        Assert.True(ownership.TryClaim(operation, out Guid second));

        Assert.False(observed.IsCurrent);

        Assert.True(ownership.Observe(operation)!.IsCurrent);

        Assert.True(ownership.Release(operation, second));
    }
}
