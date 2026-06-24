using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpServerRegistrationComparerTests
{

    [Fact]
    public void Equals_returns_true_for_identical_registrations()
    {

        McpServerConfig left = new()
        {
            Command = "node",
            Args = ["server.js", "--port", "3000"],
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MCP_HOME"] = "/opt",
            },
        };

        McpServerConfig right = new()
        {
            Command = " node ",
            Args = ["server.js", "--port", "3000"],
            Env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MCP_HOME"] = "/opt",
            },
        };

        Assert.True(McpServerRegistrationComparer.Equals(left, right));

    }

    [Fact]
    public void Equals_returns_false_when_command_or_args_differ()
    {

        McpServerConfig baseline = new()
        {
            Command = "node",
            Args = ["a.js"],
        };

        McpServerConfig differentCommand = baseline with { Command = "python" };

        McpServerConfig differentArgs = baseline with { Args = ["b.js"] };

        Assert.False(McpServerRegistrationComparer.Equals(baseline, differentCommand));

        Assert.False(McpServerRegistrationComparer.Equals(baseline, differentArgs));

    }

    [Fact]
    public void Equals_treats_missing_env_as_equivalent_to_empty()
    {

        McpServerConfig noEnv = new() { Command = "node" };

        McpServerConfig emptyEnv = new() { Command = "node", Env = new Dictionary<string, string>() };

        Assert.True(McpServerRegistrationComparer.Equals(noEnv, emptyEnv));

    }

}
