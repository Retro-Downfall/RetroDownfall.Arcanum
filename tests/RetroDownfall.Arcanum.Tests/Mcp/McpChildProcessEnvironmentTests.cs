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

    [Fact]
    public void RemoveArcanumSecretVariables_strips_arcanum_prefixed_keys_and_keeps_others()
    {

        Dictionary<string, string?> environment = new(StringComparer.Ordinal)
        {
            ["ARCANUM_Arcanum__Providers__0__ApiKey"] = "sk-secret",
            ["arcanum_lower"] = "also-secret",
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/user",
        };

        ArcanumInternalToolServer.RemoveArcanumSecretVariables(environment);

        Assert.False(environment.ContainsKey("ARCANUM_Arcanum__Providers__0__ApiKey"));

        Assert.False(environment.ContainsKey("arcanum_lower"));

        Assert.Equal("/usr/bin", environment["PATH"]);

        Assert.Equal("/home/user", environment["HOME"]);

    }

    [Fact]
    public void ShouldStripUserEnvironment_global_server_strips_host_environment_by_default()
    {

        // A "global" MCP server is modeled by the manager as ScopeWorkingDirectory == null and its
        // config carries no opt-in to inherit the host environment, so it must strip by default
        // (otherwise ARCANUM_* provider secrets would leak into the spawned subprocess).
        McpServerConfig globalServer = new() { Command = "node", Args = ["server.js"] };

        bool stripUserEnvironment = McpConnectionManager.ShouldStripUserEnvironment(globalServer);

        Assert.True(stripUserEnvironment);

    }

}
