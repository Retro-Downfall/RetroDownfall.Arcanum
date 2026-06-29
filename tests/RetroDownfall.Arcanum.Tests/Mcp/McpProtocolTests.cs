using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpProtocolTests
{

    private static readonly McpToolListCaps DefaultCaps = new(
        MaxToolsListPages: 4,
        MaxToolsPerServer: 256,
        MaxToolsPerListPage: 64,
        MaxToolsTotalBytes: 1_048_576);

    [Theory]
    [InlineData("2024-11-05")]
    [InlineData("2026-07-28")]
    public void CreateInitializeParams_uses_requested_protocol_version(string version)
    {

        McpInitializeParams initParams = McpProtocol.CreateInitializeParams(version);

        Assert.Equal(version, initParams.ProtocolVersion);

        Assert.False(string.IsNullOrWhiteSpace(initParams.ClientInfo.Name));

        Assert.False(string.IsNullOrWhiteSpace(initParams.ClientInfo.Version));

    }

    [Fact]
    public async Task InitializeAsync_sends_initialize_then_initialized_notification()
    {

        FakeMcpClient client = new((method, _) => method == "initialize"
            ? EmptyObject()
            : throw new InvalidOperationException($"unexpected {method}"));

        bool notified = false;

        await McpProtocol.InitializeAsync(
            client,
            McpProtocol.StreamableHttpProtocolVersion,
            _ =>
            {
                notified = true;

                return Task.CompletedTask;
            },
            McpJsonSerializerContext.Default,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        (string Method, JsonElement? Params) request = Assert.Single(client.Requests);

        Assert.Equal("initialize", request.Method);

        Assert.True(notified);

        Assert.Equal(
            "2026-07-28",
            request.Params!.Value.GetProperty("protocolVersion").GetString());

    }

    [Fact]
    public async Task GetToolsAsync_maps_tools_across_pages()
    {

        int listCalls = 0;

        FakeMcpClient client = new((method, parameters) =>
        {

            if (method != "tools/list")
            {

                throw new InvalidOperationException($"unexpected {method}");

            }

            listCalls++;

            return listCalls == 1
                ? ParseResult("{\"tools\":[{\"name\":\"alpha\",\"description\":\"a\",\"inputSchema\":{}}],\"nextCursor\":\"p2\"}")
                : ParseResult("{\"tools\":[{\"name\":\"beta\",\"description\":\"b\",\"inputSchema\":{}}]}");

        });

        IReadOnlyList<McpBridgeTool> tools = await McpProtocol.GetToolsAsync(
            client,
            DefaultCaps,
            toolOutputCapBytes: 4096,
            McpJsonSerializerContext.Default,
            CancellationToken.None);

        Assert.Equal(2, tools.Count);

        Assert.Equal("alpha", tools[0].Name);

        Assert.Equal("beta", tools[1].Name);

        Assert.Equal(2, listCalls);

    }

    [Fact]
    public async Task GetToolsAsync_breaks_on_repeated_cursor_to_avoid_infinite_loop()
    {

        FakeMcpClient client = new((method, _) => method == "tools/list"
            ? ParseResult("{\"tools\":[{\"name\":\"t\",\"description\":\"d\",\"inputSchema\":{}}],\"nextCursor\":\"loop\"}")
            : throw new InvalidOperationException($"unexpected {method}"));

        IReadOnlyList<McpBridgeTool> tools = await McpProtocol.GetToolsAsync(
            client,
            DefaultCaps,
            toolOutputCapBytes: 4096,
            McpJsonSerializerContext.Default,
            CancellationToken.None);

        // page 0 (no cursor) + page 1 (cursor "loop"); page 2 sees "loop" again and stops.
        Assert.Equal(2, client.Requests.Count);

        Assert.Equal(2, tools.Count);

    }

    [Fact]
    public async Task GetToolsAsync_enforces_per_server_and_per_page_caps()
    {

        FakeMcpClient client = new((method, _) => method == "tools/list"
            ? ParseResult("{\"tools\":[{\"name\":\"one\",\"description\":\"1\",\"inputSchema\":{}},{\"name\":\"two\",\"description\":\"2\",\"inputSchema\":{}},{\"name\":\"three\",\"description\":\"3\",\"inputSchema\":{}}]}")
            : throw new InvalidOperationException($"unexpected {method}"));

        McpToolListCaps caps = new(MaxToolsListPages: 4, MaxToolsPerServer: 2, MaxToolsPerListPage: 1, MaxToolsTotalBytes: 1_048_576);

        IReadOnlyList<McpBridgeTool> tools = await McpProtocol.GetToolsAsync(
            client,
            caps,
            toolOutputCapBytes: 4096,
            McpJsonSerializerContext.Default,
            CancellationToken.None);

        Assert.Single(tools);

        Assert.Equal("one", tools[0].Name);

    }

    [Fact]
    public void ExtractResultOrThrow_returns_result_or_throws_on_error_or_missing()
    {

        JsonRpcResponse ok = new() { Id = StringId("1"), Result = EmptyObject() };

        Assert.Equal(JsonValueKind.Object, McpProtocol.ExtractResultOrThrow(ok).ValueKind);

        JsonRpcResponse error = new()
        {
            Id = StringId("1"),
            Error = new JsonRpcError { Code = -32000, Message = "boom" },
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => McpProtocol.ExtractResultOrThrow(error));

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);

        JsonRpcResponse missing = new() { Id = StringId("1") };

        Assert.Throws<InvalidOperationException>(() => McpProtocol.ExtractResultOrThrow(missing));

    }

    [Fact]
    public void FormulateRpcErrorMessage_includes_code_message_and_data()
    {

        JsonRpcError error = new()
        {
            Code = -32001,
            Message = "bad",
            Data = ParseResult("{\"detail\":\"x\"}"),
        };

        string message = McpProtocol.FormulateRpcErrorMessage(error);

        Assert.Contains("-32001", message, StringComparison.Ordinal);

        Assert.Contains("bad", message, StringComparison.Ordinal);

        Assert.Contains("detail", message, StringComparison.Ordinal);

    }

    private static JsonElement EmptyObject() => ParseResult("{}");

    private static JsonElement ParseResult(string json)
    {

        using JsonDocument doc = JsonDocument.Parse(json);

        return doc.RootElement.Clone();

    }

    private static JsonElement StringId(string id) =>
        JsonSerializer.SerializeToElement(id, McpJsonSerializerContext.Default.String);

    private sealed class FakeMcpClient(Func<string, JsonElement?, JsonElement> responder) : IMcpClient
    {

        public List<(string Method, JsonElement? Params)> Requests { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JsonElement> SendRequestAsync(
            string method,
            JsonElement? parameters,
            CancellationToken cancellationToken = default,
            TimeSpan? requestTimeout = null)
        {

            Requests.Add((method, parameters));

            return Task.FromResult(responder(method, parameters));

        }

        public Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }

}
