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

    [Fact]
    public void Restart_increments_generation_and_invalidates_stale_handler()
    {

        long generation = 0;

        long captured = ++generation;

        generation++;

        Assert.False(ManagedMcpServerEntry.IsTransportGenerationCurrent(captured, generation));

    }

}
