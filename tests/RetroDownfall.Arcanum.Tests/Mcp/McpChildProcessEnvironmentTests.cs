using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpChildProcessEnvironmentTests
{

    [Fact]
    public void BuildChildProcessEnvironment_removes_blocked_keys()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["MCP_HOME"] = "/opt/mcp",
            ["LD_PRELOAD"] = "/evil.so",
            ["MCP_ALLOWED"] = "1",
        };

        Dictionary<string, string> child = McpSecurityLimits.BuildChildProcessEnvironment(source);

        Assert.Equal("/opt/mcp", child["MCP_HOME"]);

        Assert.Equal("1", child["MCP_ALLOWED"]);

        Assert.False(child.ContainsKey("LD_PRELOAD"));

    }

    [Fact]
    public void ScrubProcessEnvironment_stripUserEnvironment_returns_explicit_env_only()
    {

        Dictionary<string, string> source = new(StringComparer.Ordinal)
        {
            ["MCP_ALLOWED"] = "1",
        };

        IReadOnlyDictionary<string, string>? scrubbed = McpSecurityLimits.ScrubProcessEnvironment(
            source,
            stripUserEnvironment: true);

        Assert.NotNull(scrubbed);

        Assert.Single(scrubbed!);

        Assert.Equal("1", scrubbed["MCP_ALLOWED"]);

    }

}
