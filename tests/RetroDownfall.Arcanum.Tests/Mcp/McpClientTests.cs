using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpClientTests
{

    private const int DefaultMaxToolsPerServer = 256;

    private const int DefaultMaxToolsPerListPage = 64;

    private const int DefaultMaxToolsTotalBytes = 1_048_576;

    private static McpClient CreateClient(
        FakeMcpTransport transport,
        int maxToolsListPages = 4,
        int maxToolsPerServer = DefaultMaxToolsPerServer,
        int maxToolsPerListPage = DefaultMaxToolsPerListPage,
        int maxToolsTotalBytes = DefaultMaxToolsTotalBytes)
    {

        return new McpClient(
            transport,
            TimeSpan.FromSeconds(5),
            maxToolsListPages,
            toolOutputCapBytes: 4096,
            maxToolsPerServer,
            maxToolsPerListPage,
            maxToolsTotalBytes);

    }

    [Fact]
    public void Constructor_rejects_invalid_maxToolsListPages()
    {

        FakeMcpTransport transport = new();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new McpClient(
                transport,
                TimeSpan.FromSeconds(5),
                maxToolsListPages: 0,
                toolOutputCapBytes: 1024,
                maxToolsPerServer: DefaultMaxToolsPerServer,
                maxToolsPerListPage: DefaultMaxToolsPerListPage,
                maxToolsTotalBytes: DefaultMaxToolsTotalBytes));

    }

    [Fact]
    public async Task InitializeAsync_sends_initialize_and_initialized_notification()
    {

        FakeMcpTransport transport = new();

        transport.SetRequestHandler(request =>
        {
            if (request.Method == "initialize")
            {
                JsonElement result = JsonSerializer.SerializeToElement(
                    new McpInitializeServerResult
                    {
                        ProtocolVersion = "2024-11-05",
                        Capabilities = new McpServerCapabilitiesWire(),
                        ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
                    },
                    McpJsonSerializerContext.Default.McpInitializeServerResult);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });
            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient client = CreateClient(transport, maxToolsListPages: 4);

        await client.InitializeAsync();

        Assert.Contains(transport.WrittenRequests, r => r.Method == "initialize");

        Assert.Contains(transport.WrittenNotifications, n => n.Method == "notifications/initialized");

    }

    [Fact]
    public async Task InitializeAsync_twice_throws_InvalidOperationException()
    {

        FakeMcpTransport transport = new();

        transport.SetRequestHandler(request =>
        {
            if (request.Method == "initialize")
            {
                JsonElement result = JsonSerializer.SerializeToElement(
                    new McpInitializeServerResult
                    {
                        ProtocolVersion = "2024-11-05",
                        Capabilities = new McpServerCapabilitiesWire(),
                        ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
                    },
                    McpJsonSerializerContext.Default.McpInitializeServerResult);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });
            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient client = CreateClient(transport, maxToolsListPages: 4);

        await client.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync());

    }

    [Fact]
    public async Task SendRequestAsync_returns_result_and_maps_json_rpc_error()
    {

        FakeMcpTransport transport = new();

        transport.SetRequestHandler(request =>
        {
            if (request.Method == "initialize")
            {
                JsonElement initResult = JsonSerializer.SerializeToElement(
                    new McpInitializeServerResult
                    {
                        ProtocolVersion = "2024-11-05",
                        Capabilities = new McpServerCapabilitiesWire(),
                        ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
                    },
                    McpJsonSerializerContext.Default.McpInitializeServerResult);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = initResult,
                });
            }

            if (request.Method == "ping")
            {
                JsonElement result = JsonSerializer.SerializeToElement(
                    new McpEmptyJsonObject(),
                    McpJsonSerializerContext.Default.McpEmptyJsonObject);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });
            }

            if (request.Method == "fail")
            {
                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Error = new JsonRpcError { Code = -32000, Message = "boom" },
                });
            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient client = CreateClient(transport, maxToolsListPages: 4);

        await client.InitializeAsync();

        JsonElement ok = await client.SendRequestAsync("ping", null);

        Assert.Equal(JsonValueKind.Object, ok.ValueKind);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendRequestAsync("fail", null));

        Assert.Contains("-32000", ex.Message, StringComparison.Ordinal);

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task GetToolsAsync_maps_tools_and_pagination()
    {

        FakeMcpTransport transport = new();

        int listCalls = 0;

        transport.SetRequestHandler(request =>
        {
            if (request.Method == "initialize")
            {
                JsonElement initResult = JsonSerializer.SerializeToElement(
                    new McpInitializeServerResult
                    {
                        ProtocolVersion = "2024-11-05",
                        Capabilities = new McpServerCapabilitiesWire(),
                        ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
                    },
                    McpJsonSerializerContext.Default.McpInitializeServerResult);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = initResult,
                });
            }

            if (request.Method == "tools/list")
            {
                listCalls++;

                McpToolsListResultWire page = listCalls == 1
                    ? new()
                    {
                        Tools =
                        [
                            new McpToolDefinitionWire
                            {
                                Name = "alpha",
                                Description = "first",
                                InputSchema = JsonSerializer.SerializeToElement(
                                    new McpEmptyJsonObject(),
                                    McpJsonSerializerContext.Default.McpEmptyJsonObject),
                            },
                        ],
                    }
                    : new()
                    {
                        Tools =
                        [
                            new McpToolDefinitionWire
                            {
                                Name = "beta",
                                Description = "second",
                                InputSchema = JsonSerializer.SerializeToElement(
                                    new McpEmptyJsonObject(),
                                    McpJsonSerializerContext.Default.McpEmptyJsonObject),
                            },
                        ],
                    };

                JsonElement result = JsonSerializer.SerializeToElement(page, McpJsonSerializerContext.Default.McpToolsListResultWire);

                if (listCalls == 1)
                {
                    using JsonDocument doc = JsonDocument.Parse(result.GetRawText());

                    using MemoryStream stream = new();

                    using (Utf8JsonWriter writer = new(stream))
                    {
                        writer.WriteStartObject();

                        writer.WritePropertyName("tools");

                        doc.RootElement.GetProperty("tools").WriteTo(writer);

                        writer.WriteString("nextCursor", "page-2");

                        writer.WriteEndObject();
                    }

                    result = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
                }

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });
            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient client = CreateClient(transport, maxToolsListPages: 4);

        await client.InitializeAsync();

        IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync();

        Assert.Equal(2, tools.Count);

        Assert.Equal("alpha", tools[0].Name);

        Assert.Equal("beta", tools[1].Name);

        Assert.Equal(2, listCalls);

    }

    [Fact]
    public async Task GetToolsAsync_enforces_max_tools_per_server_and_per_page()
    {

        FakeMcpTransport transport = new();

        transport.SetRequestHandler(request =>
        {
            if (request.Method == "initialize")
            {
                JsonElement initResult = JsonSerializer.SerializeToElement(
                    new McpInitializeServerResult
                    {
                        ProtocolVersion = "2024-11-05",
                        Capabilities = new McpServerCapabilitiesWire(),
                        ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
                    },
                    McpJsonSerializerContext.Default.McpInitializeServerResult);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = initResult,
                });
            }

            if (request.Method == "tools/list")
            {
                McpToolsListResultWire page = new()
                {
                    Tools =
                    [
                        new McpToolDefinitionWire
                        {
                            Name = "one",
                            Description = "first",
                            InputSchema = JsonSerializer.SerializeToElement(
                                new McpEmptyJsonObject(),
                                McpJsonSerializerContext.Default.McpEmptyJsonObject),
                        },
                        new McpToolDefinitionWire
                        {
                            Name = "two",
                            Description = "second",
                            InputSchema = JsonSerializer.SerializeToElement(
                                new McpEmptyJsonObject(),
                                McpJsonSerializerContext.Default.McpEmptyJsonObject),
                        },
                        new McpToolDefinitionWire
                        {
                            Name = "three",
                            Description = "third",
                            InputSchema = JsonSerializer.SerializeToElement(
                                new McpEmptyJsonObject(),
                                McpJsonSerializerContext.Default.McpEmptyJsonObject),
                        },
                    ],
                };

                JsonElement result = JsonSerializer.SerializeToElement(
                    page,
                    McpJsonSerializerContext.Default.McpToolsListResultWire);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = result,
                });
            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient client = CreateClient(
            transport,
            maxToolsPerServer: 2,
            maxToolsPerListPage: 1);

        await client.InitializeAsync();

        IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync();

        Assert.Single(tools);

        Assert.Equal("one", tools[0].Name);

    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("42", "42")]
    public void NormalizeRpcId_handles_string_and_numeric_ids(string idJson, string expected)
    {

        JsonElement id = JsonDocument.Parse(idJson).RootElement;

        string normalized = McpClient.NormalizeRpcId(id);

        Assert.Equal(expected, normalized);

    }

    [Fact]
    public async Task SendRequestAsync_per_request_timeout_cancels_broker_linked_token()
    {

        McpRequestCancellationBroker broker = new();

        FakeMcpTransport transport = new();

        TaskCompletionSource brokerCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        transport.SetRequestHandler(request =>
        {
            if (request.Method == "initialize")
            {
                JsonElement initResult = JsonSerializer.SerializeToElement(
                    new McpInitializeServerResult
                    {
                        ProtocolVersion = "2024-11-05",
                        Capabilities = new McpServerCapabilitiesWire(),
                        ServerInfo = new McpServerInfoWire { Name = "test", Version = "1.0" },
                    },
                    McpJsonSerializerContext.Default.McpInitializeServerResult);

                return Task.FromResult<JsonRpcResponse?>(new JsonRpcResponse
                {
                    Id = request.Id!.Value,
                    Result = initResult,
                });
            }

            if (request.Method == "slow")
            {
                string requestId = McpClient.NormalizeRpcId(request.Id!.Value);

                CancellationToken toolToken = broker.GetTokenOrFallback(requestId, CancellationToken.None);

                toolToken.Register(() => brokerCancelled.TrySetResult());

                return Task.FromResult<JsonRpcResponse?>(null);

            }

            return Task.FromResult<JsonRpcResponse?>(null);

        });

        await using McpClient client = new(
            transport,
            TimeSpan.FromSeconds(5),
            maxToolsListPages: 4,
            toolOutputCapBytes: 4096,
            DefaultMaxToolsPerServer,
            DefaultMaxToolsPerListPage,
            DefaultMaxToolsTotalBytes,
            broker);

        await client.InitializeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendRequestAsync("slow", null, CancellationToken.None, TimeSpan.FromMilliseconds(100)));

        await brokerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

    }

}
