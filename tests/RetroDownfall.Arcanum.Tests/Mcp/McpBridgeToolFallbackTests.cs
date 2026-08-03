using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpBridgeToolFallbackTests
{

    // W3.4 Group C #6: a transport/connectivity failure on the local server (channel closed /
    // server down) must trigger the global fallback. SdkMcpClientWrapper surfaces
    // transport/connectivity failures as McpTransportUnavailableException (or a raw IOException
    // etc. from the SDK); McpBridgeTool catches those and retries on the fallback client.
    [Fact]
    public async Task InvokeCoreAsync_transport_failure_invokes_global_fallback()
    {

        FakeMcpClient localClient = new(
            _ => throw new McpTransportUnavailableException(
                "local server unavailable before dispatch",
                McpRequestDispatchState.NotDispatched));

        FakeMcpClient fallbackClient = new(_ => Task.FromResult(TextResult("fallback ok")));

        McpBridgeTool tool = new(
            "test_tool",
            "description",
            EmptySchema(),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        object? result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.NotNull(result);

        Assert.Contains("fallback ok", result!.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, fallbackClient.CallCount);

    }

    [Fact]
    public async Task InvokeCoreAsync_post_dispatch_timeout_does_not_invoke_fallback()
    {

        FakeMcpClient localClient = new(
            _ => throw new McpTransportUnavailableException(
                "tool call timed out after dispatch",
                new TimeoutException()));

        FakeMcpClient fallbackClient = new(
            _ => Task.FromResult(TextResult("must not be called")));

        McpBridgeTool tool = new(
            "mutating_tool",
            "description",
            EmptySchema(),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        await Assert.ThrowsAsync<McpTransportUnavailableException>(
            () => tool.InvokeAsync(
                new AIFunctionArguments(),
                CancellationToken.None).AsTask());

        Assert.Equal(0, fallbackClient.CallCount);

    }

    // W3.4 Group C #6: a tool-execution error (tools/call returned isError: true) must NOT
    // trigger the fallback. The tool already ran (possibly with side effects); re-running it
    // on the fallback server could double-execute a mutating operation.
    [Fact]
    public async Task InvokeCoreAsync_tool_execution_error_does_not_invoke_fallback()
    {

        FakeMcpClient localClient = new(_ => Task.FromResult(TextResult("tool failed", isError: true)));

        FakeMcpClient fallbackClient = new(_ => Task.FromResult(TextResult("should not be called")));

        McpBridgeTool tool = new(
            "test_tool",
            "description",
            EmptySchema(),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None).AsTask());

        Assert.Equal(0, fallbackClient.CallCount);

    }

    // W3.4 Group C #6: a protocol-level error (the server returned a JSON-RPC error object, not
    // isError: true — surfaced by the SDK as McpProtocolException) is also a tool-execution
    // failure and must NOT trigger the fallback.
    [Fact]
    public async Task InvokeCoreAsync_protocol_error_does_not_invoke_fallback()
    {

        FakeMcpClient localClient = new(_ => throw new ModelContextProtocol.McpProtocolException("server-side tool error"));

        FakeMcpClient fallbackClient = new(_ => Task.FromResult(TextResult("should not be called")));

        McpBridgeTool tool = new(
            "test_tool",
            "description",
            EmptySchema(),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None).AsTask());

        Assert.Equal(0, fallbackClient.CallCount);

    }

    [Fact]
    public async Task Trusted_structured_result_requires_internal_bridge_marker()
    {
        FakeMcpClient client = new(_ => Task.FromResult(TextResult("""{"status":"ok"}""")));
        McpBridgeTool untrusted = new(
            ToolRiskClassifier.SearchWorkspaceToolName,
            "description",
            EmptySchema(),
            client,
            toolOutputCapBytes: 4096);
        McpBridgeTool trusted = untrusted.WithTrustedStructuredResult(
            TrustedStructuredToolResultKind.WorkspaceSearch);

        object? untrustedResult = await untrusted.InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None);
        object? trustedResult = await trusted.InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None);

        Assert.IsType<string>(untrustedResult);
        TrustedStructuredToolResult marker =
            Assert.IsType<TrustedStructuredToolResult>(trustedResult);
        Assert.Equal(TrustedStructuredToolResultKind.WorkspaceSearch, marker.Kind);
        Assert.Equal("""{"status":"ok"}""", marker.Text);
    }

    [Fact]
    public async Task Trusted_internal_bridge_does_not_mark_external_fallback_payload()
    {
        FakeMcpClient localClient = new(
            _ => throw new McpTransportUnavailableException(
                "internal server unavailable before dispatch",
                McpRequestDispatchState.NotDispatched));
        FakeMcpClient externalFallback = new(
            _ => Task.FromResult(TextResult("""{"status":"ok"}""")));
        McpBridgeTool tool = new McpBridgeTool(
                ToolRiskClassifier.SearchWorkspaceToolName,
                "description",
                EmptySchema(),
                localClient,
                toolOutputCapBytes: 4096,
                fallbackClient: externalFallback)
            .WithTrustedStructuredResult(
                TrustedStructuredToolResultKind.WorkspaceSearch);

        object? result = await tool.InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None);

        Assert.IsType<string>(result);
    }

    [Fact]
    public async Task Workspace_check_bridge_uses_process_timeout_plus_cleanup_grace()
    {

        FakeMcpClient client = new(
            _ => Task.FromResult(TextResult("""{"status":"ok"}""")));
        McpBridgeTool tool = new McpBridgeTool(
                ToolRiskClassifier.WorkspaceCheckToolName,
                "description",
                EmptySchema(),
                client,
                toolOutputCapBytes: 4096)
            .WithRequestTimeout(TimeSpan.FromSeconds(330));

        _ = await tool.InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(330), client.LastRequestTimeout);
    }

    [Theory]
    [InlineData("ask_human")]
    [InlineData("execute_command")]
    [InlineData(ArcanumBuiltInToolNames.RunSpellScript)]
    public async Task Caller_cancellation_owned_tools_disable_mcp_request_timeout(string toolName)
    {

        FakeMcpClient client = new(
            _ => Task.FromResult(TextResult("ok")));

        McpBridgeTool tool = new McpBridgeTool(
                toolName,
                "description",
                EmptySchema(),
                client,
                toolOutputCapBytes: 4096)
            .WithRequestTimeout(TimeSpan.FromSeconds(1));

        _ = await tool.InvokeAsync(
            new AIFunctionArguments(),
            CancellationToken.None);

        Assert.Equal(Timeout.InfiniteTimeSpan, client.LastRequestTimeout);

    }

    private static JsonElement EmptySchema() => JsonDocument.Parse("{}").RootElement.Clone();

    private static CallToolResult TextResult(string text, bool isError = false) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };

    private sealed class FakeMcpClient(Func<string, Task<CallToolResult>> onCallTool) : IMcpClient
    {

        public int CallCount { get; private set; }

        public TimeSpan? LastRequestTimeout { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CallToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequestTimeout = requestTimeout;

            return onCallTool(toolName);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

}
