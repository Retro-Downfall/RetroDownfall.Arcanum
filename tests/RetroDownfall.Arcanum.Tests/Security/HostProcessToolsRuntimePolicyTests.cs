using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

public sealed class HostProcessToolsRuntimePolicyTests
{
    [Fact]
    public void Publish_refuses_to_relax_a_published_block()
    {
        HostProcessToolsRuntimePolicy policy = new();

        HostProcessToolsStartupDecision block = new(
            HostProcessToolsMarkerPairDisposition.Clean,
            CovenantPermitted: false,
            HostProcessToolsPermitted: false,
            HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition);

        Assert.True(policy.Publish(block).IsSuccess);

        Result relaxed = policy.Publish(
            new HostProcessToolsStartupDecision(
                HostProcessToolsMarkerPairDisposition.Clean,
                CovenantPermitted: false,
                HostProcessToolsPermitted: true));

        Assert.True(relaxed.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, relaxed.Error.Code);

        Assert.False(policy.HostProcessToolsPermitted);

        Assert.Equal(HostProcessToolsStartupBlocker.EscapeHatchWithoutTransition, policy.Blocker);

        // Repeating the same block stays a no-op success.
        Assert.True(policy.Publish(block).IsSuccess);
    }
}
