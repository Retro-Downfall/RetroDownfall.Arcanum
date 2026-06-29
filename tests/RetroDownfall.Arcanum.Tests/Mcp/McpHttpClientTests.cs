using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Infrastructure.Mcp;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpHttpClientTests
{

    private const string InitializeResult =
        "{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"t\",\"version\":\"1\"}}";

    [Fact]
    public async Task InitializeAsync_posts_initialize_and_initialized_with_spec_headers()
    {

        StubHttpMessageHandler handler = new(probe => probe.Method switch
        {
            "initialize" => RpcResult(probe.Id, InitializeResult),
            "notifications/initialized" => Accepted(),
            _ => Status(HttpStatusCode.BadRequest),
        });

        await using McpHttpClient client = CreateClient(handler);

        await client.InitializeAsync();

        JsonRpcProbe init = handler.Requests.First(r => r.Method == "initialize");

        Assert.Equal("2026-07-28", init.Request.Headers.GetValues(McpHttpTransport.ProtocolVersionHeader).Single());

        Assert.Equal("initialize", init.Request.Headers.GetValues(McpHttpTransport.MethodHeader).Single());

        string accept = init.Request.Headers.Accept.ToString();

        Assert.Contains("application/json", accept, StringComparison.Ordinal);

        Assert.Contains("text/event-stream", accept, StringComparison.Ordinal);

        Assert.Contains(handler.Requests, r => r.Method == "notifications/initialized");

    }

    [Fact]
    public async Task InitializeAsync_twice_throws_invalid_operation()
    {

        StubHttpMessageHandler handler = new(StandardResponder);

        await using McpHttpClient client = CreateClient(handler);

        await client.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InitializeAsync());

    }

    [Fact]
    public async Task GetToolsAsync_paginates_via_next_cursor()
    {

        StubHttpMessageHandler handler = new(probe =>
        {

            switch (probe.Method)
            {

                case "initialize":

                    return RpcResult(probe.Id, InitializeResult);

                case "notifications/initialized":

                    return Accepted();

                case "tools/list":

                    bool firstPage = !probe.Body.Contains("\"cursor\"", StringComparison.Ordinal);

                    string result = firstPage
                        ? "{\"tools\":[{\"name\":\"alpha\",\"description\":\"a\",\"inputSchema\":{}}],\"nextCursor\":\"p2\"}"
                        : "{\"tools\":[{\"name\":\"beta\",\"description\":\"b\",\"inputSchema\":{}}]}";

                    return RpcResult(probe.Id, result);

                default:

                    return Status(HttpStatusCode.BadRequest);

            }

        });

        await using McpHttpClient client = CreateClient(handler);

        await client.InitializeAsync();

        IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync();

        Assert.Equal(2, tools.Count);

        Assert.Equal("alpha", tools[0].Name);

        Assert.Equal("beta", tools[1].Name);

    }

    [Fact]
    public async Task SendRequestAsync_tools_call_returns_json_result_and_sets_mcp_name_header()
    {

        StubHttpMessageHandler handler = new(probe => probe.Method switch
        {
            "tools/call" => RpcResult(probe.Id, "{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"isError\":false}"),
            _ => Status(HttpStatusCode.BadRequest),
        });

        await using McpHttpClient client = CreateClient(handler);

        JsonElement result = await client.SendRequestAsync("tools/call", ToolsCallParams("echo"));

        Assert.True(result.TryGetProperty("content", out _));

        Assert.Equal("echo", handler.Requests[0].Request.Headers.GetValues(McpHttpTransport.NameHeader).Single());

    }

    [Fact]
    public async Task SendRequestAsync_parses_sse_stream_and_returns_final_response()
    {

        StubHttpMessageHandler handler = new(probe =>
        {

            if (probe.Method != "tools/call")
            {

                return Status(HttpStatusCode.BadRequest);

            }

            string sse =
                "data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":50}}\n\n" +
                "data: {\"jsonrpc\":\"2.0\",\"id\":\"" + probe.Id + "\",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"sse-final\"}],\"isError\":false}}\n\n";

            return Sse(sse);

        });

        await using McpHttpClient client = CreateClient(handler);

        JsonElement result = await client.SendRequestAsync("tools/call", ToolsCallParams("stream"));

        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;

        Assert.Equal("sse-final", text);

    }

    [Fact]
    public async Task SendRequestAsync_multi_round_input_required_elicits_and_reposts()
    {

        StubHttpMessageHandler handler = new(probe =>
        {

            if (probe.Method != "tools/call")
            {

                return Status(HttpStatusCode.BadRequest);

            }

            if (!probe.HasInputResponses)
            {

                return RpcResult(
                    probe.Id,
                    "{\"inputRequired\":true,\"inputRequests\":[{\"id\":\"q1\",\"prompt\":\"name?\"}],\"requestState\":{\"s\":1}}");

            }

            Assert.Contains("\"Alice\"", probe.Body, StringComparison.Ordinal);

            Assert.Contains("\"requestState\"", probe.Body, StringComparison.Ordinal);

            return RpcResult(probe.Id, "{\"content\":[{\"type\":\"text\",\"text\":\"done Alice\"}],\"isError\":false}");

        });

        FakeInputElicitor elicitor = new(requests =>
            requests.Select(r => new McpInputResponse { Id = r.Id, Value = "Alice" }).ToArray());

        await using McpHttpClient client = CreateClient(handler, elicitor);

        JsonElement result = await client.SendRequestAsync("tools/call", ToolsCallParams("ask"));

        Assert.Equal("done Alice", result.GetProperty("content")[0].GetProperty("text").GetString());

        Assert.Equal(1, elicitor.Calls);

        Assert.Equal(2, handler.Requests.Count(r => r.Method == "tools/call"));

    }

    [Fact]
    public async Task SendRequestAsync_input_required_without_elicitor_throws_invalid_operation()
    {

        StubHttpMessageHandler handler = new(probe =>
            RpcResult(probe.Id, "{\"inputRequired\":true,\"inputRequests\":[{\"id\":\"q1\"}],\"requestState\":{}}"));

        await using McpHttpClient client = CreateClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendRequestAsync("tools/call", ToolsCallParams("ask")));

    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task SendRequestAsync_http_error_status_throws_transport_unavailable(HttpStatusCode status)
    {

        StubHttpMessageHandler handler = new(_ => Status(status));

        await using McpHttpClient client = CreateClient(handler);

        await Assert.ThrowsAsync<McpTransportUnavailableException>(() =>
            client.SendRequestAsync("tools/call", ToolsCallParams("x")));

    }

    [Fact]
    public async Task SendRequestAsync_connection_failure_throws_transport_unavailable()
    {

        StubHttpMessageHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        await using McpHttpClient client = CreateClient(handler);

        await Assert.ThrowsAsync<McpTransportUnavailableException>(() =>
            client.SendRequestAsync("tools/call", ToolsCallParams("x")));

    }

    [Fact]
    public async Task SendRequestAsync_jsonrpc_error_throws_invalid_operation_without_wrapping()
    {

        StubHttpMessageHandler handler = new(probe =>
        {

            string body = "{\"jsonrpc\":\"2.0\",\"id\":\"" + probe.Id + "\",\"error\":{\"code\":-32000,\"message\":\"boom\"}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

        });

        await using McpHttpClient client = CreateClient(handler);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendRequestAsync("tools/call", ToolsCallParams("err")));

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);

    }

    [Fact]
    public async Task SendRequestAsync_per_request_timeout_throws_transport_unavailable()
    {

        StubHttpMessageHandler handler = new(async (_, ct) =>
        {

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            return new HttpResponseMessage(HttpStatusCode.OK);

        });

        await using McpHttpClient client = CreateClient(handler);

        await Assert.ThrowsAsync<McpTransportUnavailableException>(() =>
            client.SendRequestAsync("tools/call", ToolsCallParams("slow"), CancellationToken.None, TimeSpan.FromMilliseconds(100)));

    }

    [Fact]
    public async Task SendRequestAsync_caller_cancellation_aborts_request_and_throws_operation_canceled()
    {

        using CancellationTokenSource cts = new();

        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        StubHttpMessageHandler handler = new(async (probe, ct) =>
        {

            received.TrySetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            return RpcResult(probe.Id, "{}");

        });

        await using McpHttpClient client = CreateClient(handler);

        Task<JsonElement> call = client.SendRequestAsync("tools/call", ToolsCallParams("block"), cts.Token);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.Single(handler.Requests);

    }

    [Fact]
    public async Task SendRequestAsync_oversized_response_throws_line_size_exceeded()
    {

        string big = new('x', 2048);

        StubHttpMessageHandler handler = new(probe =>
            RpcResult(probe.Id, "{\"content\":[{\"type\":\"text\",\"text\":\"" + big + "\"}]}"));

        await using McpHttpClient client = CreateClient(handler, maxJsonRpcLineBytes: 512);

        await Assert.ThrowsAsync<McpLineSizeExceededException>(() =>
            client.SendRequestAsync("tools/call", ToolsCallParams("big")));

    }

    [Fact]
    public async Task Constructor_rejects_non_http_scheme()
    {

        using HttpClient http = new();

        Assert.Throws<ArgumentException>(() => new McpHttpClient(
            new Uri("ftp://example.com/rpc"),
            http,
            TimeSpan.FromSeconds(30),
            maxToolsListPages: 4,
            toolOutputCapBytes: 4096,
            maxToolsPerServer: 256,
            maxToolsPerListPage: 64,
            maxToolsTotalBytes: 1_048_576,
            maxJsonRpcLineBytes: 1_048_576));

    }

    private static HttpResponseMessage StandardResponder(JsonRpcProbe probe) => probe.Method switch
    {
        "initialize" => RpcResult(probe.Id, InitializeResult),
        "notifications/initialized" => Accepted(),
        _ => Status(HttpStatusCode.BadRequest),
    };

    private static McpHttpClient CreateClient(
        HttpMessageHandler handler,
        IMcpInputElicitor? elicitor = null,
        int maxJsonRpcLineBytes = 1_048_576)
    {

        HttpClient http = new(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        return new McpHttpClient(
            new Uri("https://mcp.example.com/rpc"),
            http,
            TimeSpan.FromSeconds(30),
            maxToolsListPages: 4,
            toolOutputCapBytes: 4096,
            maxToolsPerServer: 256,
            maxToolsPerListPage: 64,
            maxToolsTotalBytes: 1_048_576,
            maxJsonRpcLineBytes: maxJsonRpcLineBytes,
            elicitor);

    }

    private static JsonElement ToolsCallParams(string name)
    {

        using JsonDocument doc = JsonDocument.Parse("{\"name\":\"" + name + "\",\"arguments\":{}}");

        return doc.RootElement.Clone();

    }

    private static HttpResponseMessage RpcResult(string id, string resultJson)
    {

        string body = "{\"jsonrpc\":\"2.0\",\"id\":\"" + id + "\",\"result\":" + resultJson + "}";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    }

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status)
        {
            Content = new StringContent("error", Encoding.UTF8, "text/plain"),
        };

    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted);

    private sealed record JsonRpcProbe(
        HttpRequestMessage Request,
        string Body,
        string Method,
        string Id,
        bool HasInputResponses)
    {

        public static JsonRpcProbe Parse(HttpRequestMessage request, string body)
        {

            using JsonDocument doc = JsonDocument.Parse(body);

            JsonElement root = doc.RootElement;

            string method = root.TryGetProperty("method", out JsonElement m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : string.Empty;

            string id = root.TryGetProperty("id", out JsonElement idEl)
                ? idEl.ValueKind == JsonValueKind.String ? idEl.GetString() ?? string.Empty : idEl.GetRawText()
                : string.Empty;

            bool hasInputResponses = root.TryGetProperty("params", out JsonElement p)
                && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("inputResponses", out _);

            return new JsonRpcProbe(request, body, method, id, hasInputResponses);

        }

    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {

        private readonly Func<JsonRpcProbe, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHttpMessageHandler(Func<JsonRpcProbe, CancellationToken, Task<HttpResponseMessage>> responder)
        {

            _responder = responder;

        }

        public StubHttpMessageHandler(Func<JsonRpcProbe, HttpResponseMessage> responder)
            : this((probe, _) => Task.FromResult(responder(probe)))
        {
        }

        public List<JsonRpcProbe> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            JsonRpcProbe probe = JsonRpcProbe.Parse(request, body);

            Requests.Add(probe);

            return await _responder(probe, cancellationToken).ConfigureAwait(false);

        }

    }

    private sealed class FakeInputElicitor(
        Func<IReadOnlyList<McpInputRequest>, IReadOnlyList<McpInputResponse>> map) : IMcpInputElicitor
    {

        public int Calls { get; private set; }

        public Task<IReadOnlyList<McpInputResponse>> ElicitAsync(
            IReadOnlyList<McpInputRequest> requests,
            CancellationToken cancellationToken)
        {

            Calls++;

            return Task.FromResult(map(requests));

        }

    }

}
