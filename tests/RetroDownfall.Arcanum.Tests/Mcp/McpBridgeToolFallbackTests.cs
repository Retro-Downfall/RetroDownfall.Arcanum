using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpBridgeToolFallbackTests
{

    private const int DefaultMaxToolsPerServer = 256;

    private const int DefaultMaxToolsPerListPage = 64;

    private const int DefaultMaxToolsTotalBytes = 1_048_576;

    // W3.4 Group C #6: a transport/connectivity failure on the local server (channel closed /
    // server down) must trigger the global fallback. McpClient wraps transport-write failures
    // as McpTransportUnavailableException; McpBridgeTool catches that and retries on the
    // fallback client.
    [Fact]
    public async Task InvokeCoreAsync_transport_failure_invokes_global_fallback()
    {

        FakeMcpTransport localTransport = new();

        FakeMcpTransport fallbackTransport = new();

        localTransport.SetRequestHandler(request =>
        {

            if (request.Method == "initialize")
            {

                return Task.FromResult<JsonRpcResponse?>(InitializeResponse(request));

            }

            // tools/call: simulate a transport-layer failure (the local server channel closed).
            throw new ChannelClosedException("local server channel closed");

        });

        fallbackTransport.SetRequestHandler(request =>
        {

            if (request.Method == "initialize")
            {

                return Task.FromResult<JsonRpcResponse?>(InitializeResponse(request));

            }

            if (request.Method == "tools/call")
            {

                JsonElement result = JsonSerializer.SerializeToElement(
                    new McpToolsCallResultWire
                    {
                        Content =
                        [
                            new McpToolContentTextWire { Text = "fallback ok" },
                        ],
                        IsError = false,
                    },
                    McpJsonSerializerContext.Default.McpToolsCallResultWire);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });

            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient localClient = MakeClient(localTransport);

        await using McpClient fallbackClient = MakeClient(fallbackTransport);

        await localClient.InitializeAsync();

        await fallbackClient.InitializeAsync();

        McpBridgeTool tool = new(
            "test_tool",
            "description",
            JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), McpJsonSerializerContext.Default.McpEmptyJsonObject),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        object? result = await tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        Assert.NotNull(result);

        Assert.Contains("fallback ok", result!.ToString(), StringComparison.Ordinal);

        Assert.Contains(fallbackTransport.WrittenRequests, r => r.Method == "tools/call");

    }

    // W3.4 Group C #6: a tool-execution error (tools/call returned isError: true) must NOT
    // trigger the fallback. The tool already ran (possibly with side effects); re-running it
    // on the fallback server could double-execute a mutating operation. The
    // InvalidOperationException from the isError payload must propagate without a fallback
    // attempt.
    [Fact]
    public async Task InvokeCoreAsync_tool_execution_error_does_not_invoke_fallback()
    {

        FakeMcpTransport localTransport = new();

        FakeMcpTransport fallbackTransport = new();

        localTransport.SetRequestHandler(request =>
        {

            if (request.Method == "initialize")
            {

                return Task.FromResult<JsonRpcResponse?>(InitializeResponse(request));

            }

            if (request.Method == "tools/call")
            {

                JsonElement result = JsonSerializer.SerializeToElement(
                    new McpToolsCallResultWire
                    {
                        Content =
                        [
                            new McpToolContentTextWire { Text = "tool failed" },
                        ],
                        IsError = true,
                    },
                    McpJsonSerializerContext.Default.McpToolsCallResultWire);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });

            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        fallbackTransport.SetRequestHandler(request =>
        {

            if (request.Method == "initialize")
            {

                return Task.FromResult<JsonRpcResponse?>(InitializeResponse(request));

            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient localClient = MakeClient(localTransport);

        await using McpClient fallbackClient = MakeClient(fallbackTransport);

        await localClient.InitializeAsync();

        await fallbackClient.InitializeAsync();

        McpBridgeTool tool = new(
            "test_tool",
            "description",
            JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), McpJsonSerializerContext.Default.McpEmptyJsonObject),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None).AsTask());

        Assert.DoesNotContain(fallbackTransport.WrittenRequests, r => r.Method == "tools/call");

    }

    // W3.4 Group C #6: a JSON-RPC error response (the server returned an error object, not
    // isError: true) is also a tool-execution failure and must NOT trigger the fallback.
    [Fact]
    public async Task InvokeCoreAsync_jsonrpc_error_response_does_not_invoke_fallback()
    {

        FakeMcpTransport localTransport = new();

        FakeMcpTransport fallbackTransport = new();

        localTransport.SetRequestHandler(request =>
        {

            if (request.Method == "initialize")
            {

                return Task.FromResult<JsonRpcResponse?>(InitializeResponse(request));

            }

            if (request.Method == "tools/call")
            {

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Error = new JsonRpcError { Code = -32000, Message = "server-side tool error" },
                });

            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        fallbackTransport.SetRequestHandler(request =>
        {

            if (request.Method == "initialize")
            {

                return Task.FromResult<JsonRpcResponse?>(InitializeResponse(request));

            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient localClient = MakeClient(localTransport);

        await using McpClient fallbackClient = MakeClient(fallbackTransport);

        await localClient.InitializeAsync();

        await fallbackClient.InitializeAsync();

        McpBridgeTool tool = new(
            "test_tool",
            "description",
            JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), McpJsonSerializerContext.Default.McpEmptyJsonObject),
            localClient,
            toolOutputCapBytes: 4096,
            fallbackClient: fallbackClient);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.InvokeAsync(new AIFunctionArguments(), CancellationToken.None).AsTask());

        Assert.DoesNotContain(fallbackTransport.WrittenRequests, r => r.Method == "tools/call");

    }

    private static McpClient MakeClient(FakeMcpTransport transport) =>
        new(
            transport,
            TimeSpan.FromSeconds(5),
            maxToolsListPages: 4,
            toolOutputCapBytes: 4096,
            DefaultMaxToolsPerServer,
            DefaultMaxToolsPerListPage,
            DefaultMaxToolsTotalBytes);

    private static JsonRpcResponse InitializeResponse(JsonRpcRequest request)
    {

        JsonElement initResult = JsonSerializer.SerializeToElement(
            new McpInitializeServerResult
            {
                ProtocolVersion = "2024-11-05",
                Capabilities = new McpServerCapabilitiesWire(),
                ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
            },
            McpJsonSerializerContext.Default.McpInitializeServerResult);

        return new JsonRpcResponse
        {
            Id = request.Id!.Value,
            Result = initResult,
        };

    }

}
