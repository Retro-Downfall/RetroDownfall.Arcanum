using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpConfigTypeInferenceTests
{

    [Theory]
    [InlineData("stdio", McpServerTransport.Stdio)]
    [InlineData("http", McpServerTransport.Http)]
    [InlineData("HTTP", McpServerTransport.Http)]
    [InlineData("sse", McpServerTransport.Sse)]
    [InlineData("Sse", McpServerTransport.Sse)]
    public void InferTransport_honors_explicit_type(string type, McpServerTransport expected)
    {

        McpServerConfig cfg = new() { Type = type, Url = "https://example.com/rpc", Command = "node" };

        Assert.Equal(expected, McpConnectionManager.InferTransport(cfg));

    }

    [Fact]
    public void InferTransport_url_without_type_infers_http()
    {

        McpServerConfig cfg = new() { Url = "https://example.com/rpc" };

        Assert.Equal(McpServerTransport.Http, McpConnectionManager.InferTransport(cfg));

    }

    [Fact]
    public void InferTransport_command_without_url_or_type_infers_stdio()
    {

        McpServerConfig cfg = new() { Command = "node", Args = ["server.js"] };

        Assert.Equal(McpServerTransport.Stdio, McpConnectionManager.InferTransport(cfg));

    }

    [Fact]
    public void InferTransport_unknown_type_falls_back_to_url_inference()
    {

        McpServerConfig withUrl = new() { Type = "weird", Url = "https://example.com/rpc" };

        Assert.Equal(McpServerTransport.Http, McpConnectionManager.InferTransport(withUrl));

        McpServerConfig withoutUrl = new() { Type = "weird", Command = "node" };

        Assert.Equal(McpServerTransport.Stdio, McpConnectionManager.InferTransport(withoutUrl));

    }

    [Fact]
    public void BuildInheritEnvironmentAllowlist_null_or_empty_returns_null()
    {

        Assert.Null(McpConnectionManager.BuildInheritEnvironmentAllowlist(null));

        Assert.Null(McpConnectionManager.BuildInheritEnvironmentAllowlist([]));

        Assert.Null(McpConnectionManager.BuildInheritEnvironmentAllowlist(["   "]));

    }

    [Fact]
    public void BuildInheritEnvironmentAllowlist_trims_and_is_case_insensitive()
    {

        IReadOnlySet<string>? allowlist = McpConnectionManager.BuildInheritEnvironmentAllowlist([" PATH ", "HOME"]);

        Assert.NotNull(allowlist);

        Assert.Contains("PATH", allowlist!);

        Assert.Contains("path", allowlist);

        Assert.Contains("home", allowlist);

    }

}
