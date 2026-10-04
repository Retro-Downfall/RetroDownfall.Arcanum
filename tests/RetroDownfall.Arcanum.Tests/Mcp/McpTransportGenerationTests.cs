using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpTransportGenerationTests
{
    [Fact]
    public void IsTransportGenerationCurrent_matches_only_same_generation()
    {
        Assert.True(ManagedMcpServerEntry.IsTransportGenerationCurrent(3, 3));

        Assert.False(ManagedMcpServerEntry.IsTransportGenerationCurrent(2, 3));
    }

    // The restart-invalidates-a-stale-handler behaviour is covered against the real manager by
    // McpConnectionManagerTransportEndedTests.Stale_transport_ended_handler_does_not_dispose_restarted_client.
}
