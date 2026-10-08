using System.Net;

using System.Net.Http.Headers;

using System.Text;

using System.Text.Json;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Client;

using ModelContextProtocol.Protocol;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Tests.Mcp;

public sealed class McpHttpResponseBoundHandlerTests
{
    // The handler adds a fixed framing allowance to the configured frame bound.
    private const long FrameBytes = 1_000L;

    private const long BoundBytes = FrameBytes + McpHttpResponseBoundHandler.FramingAllowanceBytes;

    [Fact]
    public async Task A_message_within_the_bound_passes_through_with_its_headers()
    {
        string body = new('a', 2_000);

        using HttpResponseMessage response = await SendAsync(
            Respond(new StringContent(body, Encoding.UTF8, "application/json")));

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        Assert.Equal(body.Length, response.Content.Headers.ContentLength);

        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_declared_length_over_the_bound_fails_before_any_body_is_read()
    {
        EndlessStream body = new();

        StreamContent content = new(body);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        content.Headers.ContentLength = BoundBytes + 1;

        _ = await Assert.ThrowsAsync<HttpRequestException>(() => SendAsync(Respond(content)));

        Assert.Equal(0L, body.BytesServed);
    }

    [Fact]
    public async Task A_body_with_no_declared_length_stops_being_read_once_it_passes_the_bound()
    {
        EndlessStream body = new();

        StreamContent content = new(body);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await SendAsync(Respond(content));

        await using Stream stream = await response.Content.ReadAsStreamAsync();

        _ = await Assert.ThrowsAsync<IOException>(async () =>
        {
            byte[] buffer = new byte[512];

            while (await stream.ReadAsync(buffer) > 0)
            {
            }
        });

        // It stopped one byte past the bound, not at the end of an endless body.
        Assert.Equal(BoundBytes + 1, body.BytesServed);
    }

    [Fact]
    public async Task Buffered_reads_of_an_oversized_body_are_bounded_too()
    {
        EndlessStream body = new();

        StreamContent content = new(body);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await SendAsync(Respond(content));

        // HttpContent reports a failure while buffering as HttpRequestException around the cause.
        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => response.Content.ReadAsStringAsync());

        _ = Assert.IsType<IOException>(failure.InnerException);

        Assert.Equal(BoundBytes + 1, body.BytesServed);
    }

    [Fact]
    public async Task An_event_stream_is_bounded_per_event_not_in_total()
    {
        // 200 events of ~600 bytes each is ~120 KiB, far over the bound, yet no single event is.
        StringBuilder stream = new();

        for (int index = 0; index < 200; index++)
        {
            stream.Append("data: ").Append(new string('e', 600)).Append("\n\n");
        }

        using HttpResponseMessage response = await SendAsync(
            Respond(new StringContent(stream.ToString(), Encoding.UTF8, "text/event-stream")));

        Assert.Equal(stream.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("\r\r")]
    public async Task Every_blank_line_spelling_ends_an_event(string terminator)
    {
        StringBuilder stream = new();

        for (int index = 0; index < 200; index++)
        {
            stream.Append("data: ").Append(new string('e', 600)).Append(terminator);
        }

        using HttpResponseMessage response = await SendAsync(
            Respond(new StringContent(stream.ToString(), Encoding.UTF8, "text/event-stream")));

        Assert.Equal(stream.ToString(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_event_stream_event_over_the_bound_fails_the_read()
    {
        string oversizedLine = "data: " + new string('x', (int)BoundBytes + 100) + "\n\n";

        using HttpResponseMessage response = await SendAsync(
            Respond(new StringContent(oversizedLine, Encoding.UTF8, "text/event-stream")));

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => response.Content.ReadAsStringAsync());

        _ = Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task An_event_split_across_many_small_data_lines_is_still_one_bounded_event()
    {
        // Each line is tiny, so only a per-event budget can catch the sum.
        StringBuilder stream = new();

        for (int index = 0; index < 400; index++)
        {
            stream.Append("data: ").Append(new string('s', 40)).Append('\n');
        }

        stream.Append('\n');

        using HttpResponseMessage response = await SendAsync(
            Respond(new StringContent(stream.ToString(), Encoding.UTF8, "text/event-stream")));

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => response.Content.ReadAsStringAsync());

        _ = Assert.IsType<IOException>(failure.InnerException);
    }

    [Fact]
    public async Task An_SDK_session_over_the_bound_handler_fails_instead_of_buffering_an_oversized_response()
    {
        EndlessStream body = new();

        StreamContent content = new(body);

        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using McpHttpResponseBoundHandler handler = new(FrameBytes)
        {
            InnerHandler = new StubHandler(Respond(content)),
        };

        using HttpClient httpClient = new(handler, disposeHandler: false);

        HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("https://mcp.example/rpc"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);

        await using SdkMcpClientWrapper client = new(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "arcanum-tests", Version = "1.0.0" },
            },
            initializationTimeout: TimeSpan.FromSeconds(30),
            toolOutputCapBytes: 65536,
            maxToolsTotalBytes: 1_048_576,
            elicitationSink: new McpElicitationSink());

        _ = await Assert.ThrowsAnyAsync<Exception>(
            () => client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60)));

        // Equality, not an upper bound: it proves the SDK really was reading the body when the bound cut
        // it off, rather than failing earlier for an unrelated reason.
        Assert.Equal(BoundBytes + 1, body.BytesServed);
    }

    [Fact]
    public async Task An_oversized_event_on_the_server_to_client_stream_is_abandoned_after_bounded_retries_and_leaves_the_session_usable()
    {
        // The unsolicited-message stream is a long-lived GET. A hostile server that answers it with one
        // endless event is never buffered: each attempt is cut off at the bound, the SDK retries the
        // stream a bounded number of times and then stops listening. The session itself is not torn down,
        // because requests and their responses travel on POSTs; DESIGN says exactly that.
        const int MaxReconnectionAttempts = 2;

        EndlessStream eventBody = new(prefix: Encoding.UTF8.GetBytes("data: "));

        int streamOpens = 0;

        FakeStreamableHttpServer server = new(
            onGet: () =>
            {
                _ = Interlocked.Increment(ref streamOpens);

                StreamContent content = new(eventBody);

                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

                return Respond(content);
            });

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using SdkMcpClientWrapper client = CreateClient(
            server,
            maxReconnectionAttempts: MaxReconnectionAttempts,
            onTransportEnded: () => ended.TrySetResult());

        await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));

        // The first open plus every permitted retry, and then no more.
        int expectedOpens = 1 + MaxReconnectionAttempts;

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (Volatile.Read(ref streamOpens) < expectedOpens && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(expectedOpens, Volatile.Read(ref streamOpens));

        // Every attempt was cut off at the bound (plus the one byte that proves it was exceeded).
        Assert.Equal(expectedOpens * (BoundBytes + 1), eventBody.BytesServed);

        Assert.False(ended.Task.IsCompleted, "An oversized event on the server-to-client stream ended the session.");

        IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(tools);
    }

    [Fact]
    public async Task An_oversized_response_to_one_request_fails_that_call_without_ending_the_session()
    {
        EndlessStream oversized = new();

        int toolListCalls = 0;

        FakeStreamableHttpServer server = new(
            onGet: () => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            onToolsList: requestId =>
            {
                if (Interlocked.Increment(ref toolListCalls) > 1)
                {
                    return null;
                }

                StreamContent content = new(oversized);

                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                return Respond(content);
            });

        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using SdkMcpClientWrapper client = CreateClient(
            server,
            maxReconnectionAttempts: 0,
            onTransportEnded: () => ended.TrySetResult());

        await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));

        _ = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetToolsAsync().WaitAsync(TimeSpan.FromSeconds(30)));

        // Cut off at the bound rather than buffered.
        Assert.Equal(BoundBytes + 1, oversized.BytesServed);

        // The next request on the same session goes through: one hostile message costs that call, not the
        // session.
        IReadOnlyList<McpBridgeTool> tools = await client.GetToolsAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(tools);

        Assert.False(ended.Task.IsCompleted, "An oversized response to one request ended the session.");
    }

    private static SdkMcpClientWrapper CreateClient(
        HttpMessageHandler server,
        int maxReconnectionAttempts,
        Action onTransportEnded)
    {
        McpHttpResponseBoundHandler handler = new(FrameBytes)
        {
            InnerHandler = server,
        };

        HttpClient httpClient = new(handler, disposeHandler: true);

        HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("https://mcp.example/rpc"),
                TransportMode = HttpTransportMode.StreamableHttp,
                MaxReconnectionAttempts = maxReconnectionAttempts,
                DefaultReconnectionInterval = TimeSpan.FromMilliseconds(10),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);

        return new SdkMcpClientWrapper(
            transport,
            new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "arcanum-tests", Version = "1.0.0" },
            },
            initializationTimeout: TimeSpan.FromSeconds(30),
            toolOutputCapBytes: 65536,
            maxToolsTotalBytes: 1_048_576,
            elicitationSink: new McpElicitationSink())
        {
            OnTransportEnded = onTransportEnded,
        };
    }

    [Fact]
    public void The_McpHttp_named_client_pipeline_applies_the_bound()
    {
        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        using HttpMessageHandler handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(McpConnectionManager.McpHttpClientName);

        List<Type> pipeline = [];

        for (HttpMessageHandler? current = handler;
            current is DelegatingHandler delegating;
            current = delegating.InnerHandler)
        {
            pipeline.Add(delegating.GetType());
        }

        Assert.Contains(typeof(McpHttpResponseBoundHandler), pipeline);
    }

    [Fact]
    public void The_McpHttp_named_client_bound_is_the_manager_frame_cap_plus_the_framing_allowance()
    {
        ServiceCollection services = [];

        services.AddArcanumInfrastructure(new ConfigurationBuilder().Build());

        using ServiceProvider provider = services.BuildServiceProvider();

        using HttpMessageHandler handler = provider
            .GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(McpConnectionManager.McpHttpClientName);

        McpHttpResponseBoundHandler? bound = null;

        for (HttpMessageHandler? current = handler;
            current is DelegatingHandler delegating;
            current = delegating.InnerHandler)
        {
            bound ??= delegating as McpHttpResponseBoundHandler;
        }

        Assert.NotNull(bound);

        // The in-process transport and the HTTP bound enforce one code-owned cap; they must read it from
        // the same place so neither can drift when the cap changes.
        Assert.Equal(
            McpSecurityLimits.MaxJsonRpcLineBytes + McpHttpResponseBoundHandler.FramingAllowanceBytes,
            bound.BoundBytes);
    }

    [Fact]
    public void The_code_owned_frame_cap_is_clamped_in_exactly_one_place()
    {
        string infrastructureRoot = Path.Combine(
            global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot(),
            "src",
            "RetroDownfall.Arcanum.Infrastructure");

        List<string> readers = [];

        foreach (string file in Directory.EnumerateFiles(infrastructureRoot, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(infrastructureRoot, file);

            if (relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("ArcanumSettingClamps.McpMaxJsonRpcLineBytes(", StringComparison.Ordinal))
            {
                readers.Add(relative);
            }
        }

        // McpSecurityLimits.MaxJsonRpcLineBytes owns the clamp; the connection manager and the McpHttp
        // client registration both use it instead of repeating it.
        Assert.Equal([Path.Combine("Mcp", "McpSecurityLimits.cs")], readers.Order(StringComparer.Ordinal));
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpResponseMessage inner)
    {
        using McpHttpResponseBoundHandler handler = new(FrameBytes)
        {
            InnerHandler = new StubHandler(inner),
        };

        using HttpMessageInvoker invoker = new(handler, disposeHandler: false);

        return await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "https://mcp.example/rpc"),
            CancellationToken.None);
    }

    private static HttpResponseMessage Respond(HttpContent content) =>
        new(HttpStatusCode.OK) { Content = content };

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    // Answers every MCP request an initialize handshake needs, and hands the unsolicited-message GET to
    // the supplied responder.
    private sealed class FakeStreamableHttpServer(
        Func<HttpResponseMessage> onGet,
        Func<string, HttpResponseMessage?>? onToolsList = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return onGet();
            }

            if (request.Method != HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);

            using JsonDocument document = JsonDocument.Parse(body);

            JsonElement root = document.RootElement;

            string? methodName = root.TryGetProperty("method", out JsonElement method) ? method.GetString() : null;

            if (methodName == "tools/list")
            {
                string requestId = root.GetProperty("id").GetRawText();

                return onToolsList?.Invoke(requestId)
                    ?? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"jsonrpc\":\"2.0\",\"id\":" + requestId + ",\"result\":{\"tools\":[]}}",
                            Encoding.UTF8,
                            "application/json"),
                    };
            }

            if (methodName != "initialize")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            string reply =
                "{\"jsonrpc\":\"2.0\",\"id\":" + root.GetProperty("id").GetRawText()
                + ",\"result\":{\"protocolVersion\":\"" + root.GetProperty("params").GetProperty("protocolVersion").GetString() + "\""
                + ",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1\"}}}";

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };

            response.Headers.Add("Mcp-Session-Id", "arcanum-test-session");

            return response;
        }
    }

    private sealed class EndlessStream(byte[]? prefix = null) : Stream
    {
        private long _bytesServed;

        private int _prefixServed;

        public long BytesServed => Interlocked.Read(ref _bytesServed);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            // An optional leading field name (such as "data: ") is served once, then the body is endless.
            int written = 0;

            if (prefix is not null && _prefixServed < prefix.Length)
            {
                int take = Math.Min(prefix.Length - _prefixServed, buffer.Length);

                prefix.AsSpan(_prefixServed, take).CopyTo(buffer);

                _prefixServed += take;

                written = take;
            }

            buffer[written..].Fill((byte)'z');

            _ = Interlocked.Add(ref _bytesServed, buffer.Length);

            return buffer.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
