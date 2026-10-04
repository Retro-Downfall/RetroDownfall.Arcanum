using System.Net;

using System.Net.Http.Headers;

using System.Text;

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

    private sealed class EndlessStream : Stream
    {
        private long _bytesServed;

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'z', offset, count);

            _ = Interlocked.Add(ref _bytesServed, count);

            return count;
        }

        public override int Read(Span<byte> buffer)
        {
            buffer.Fill((byte)'z');

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
