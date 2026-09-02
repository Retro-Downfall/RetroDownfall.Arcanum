using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Models;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class ArcanumApiClientNdjsonTests
{
    [Fact]
    public async Task PostNdjsonStreamAsync_yields_reasoning_skips_unknown_type_and_continues()
    {
        IntelligenceEvent reasoning = new(
            IntelligenceEventType.Reasoning,
            "client-safe summary",
            Reasoning: new ReasoningContentSegment(
                "client-safe summary",
                ReasoningOutputMode.Summary));
        IntelligenceEvent token = new(IntelligenceEventType.Token, string.Empty, "answer");
        IntelligenceEvent result = new(IntelligenceEventType.Result, "answer", "answer");
        string ndjson = string.Join(
            '\n',
            JsonSerializer.Serialize(reasoning, TheForgeJsonContext.Default.IntelligenceEvent),
            """{"type":"futureThought","message":"ignore me","data":"secret"}""",
            JsonSerializer.Serialize(token, TheForgeJsonContext.Default.IntelligenceEvent),
            JsonSerializer.Serialize(result, TheForgeJsonContext.Default.IntelligenceEvent))
            + "\n";
        ListLogger<ArcanumApiClient> logger = new();
        ArcanumApiClient client = CreateClient(ndjson, logger);

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                           "/api/intelligence/ping-stream",
                           new PingRequest("hello"),
                           TheForgeJsonContext.Default.PingRequest,
                           TheForgeJsonContext.Default.IntelligenceEvent,
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(
            [
                IntelligenceEventType.Reasoning,
                IntelligenceEventType.Token,
                IntelligenceEventType.Result,
            ],
            events.Select(static evt => evt.Type));
        Assert.Equal("client-safe summary", events[0].Reasoning?.Text);
        Assert.DoesNotContain(
            logger.Messages,
            static message => message.Contains("deserialize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_logs_malformed_or_missing_type_and_continues()
    {
        IntelligenceEvent token = new(IntelligenceEventType.Token, string.Empty, "answer");
        string ndjson = string.Join(
            '\n',
            """{"type":""",
            """{"message":"missing"}""",
            """{"type":42,"message":"invalid"}""",
            JsonSerializer.Serialize(token, TheForgeJsonContext.Default.IntelligenceEvent))
            + "\n";
        ListLogger<ArcanumApiClient> logger = new();
        ArcanumApiClient client = CreateClient(ndjson, logger);

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                           "/api/intelligence/ping-stream",
                           new PingRequest("hello"),
                           TheForgeJsonContext.Default.PingRequest,
                           TheForgeJsonContext.Default.IntelligenceEvent,
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        IntelligenceEvent answer = Assert.Single(events);
        Assert.Equal(IntelligenceEventType.Token, answer.Type);
        Assert.Equal("answer", answer.Data);
        Assert.Equal(
            3,
            logger.Messages.Count(static message =>
                message.Contains("deserialize", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_fragmented_frames_mirror_strict_type_semantics_and_continue()
    {
        string ndjson = string.Join(
            '\n',
            """{"type":"TOKEN","message":"","data":"upper"}""",
            """{"type":"futureThought","message":"skip"}""",
            """{"type":" token ","message":"padded"}""",
            """{"type":"","message":"blank"}""",
            """{"type":"   ","message":"whitespace"}""",
            """{"message":"missing"}""",
            """{"type":42,"message":"numeric"}""",
            """{"type":"token","message":"","data":"tail"}""")
            + "\n";
        ListLogger<ArcanumApiClient> logger = new();
        ArcanumApiClient client = CreateClient(
            new NdjsonHandler(ndjson, maxChunkBytes: 2),
            logger);

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                           "/api/intelligence/ping-stream",
                           new PingRequest("hello"),
                           TheForgeJsonContext.Default.PingRequest,
                           TheForgeJsonContext.Default.IntelligenceEvent,
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(
            [IntelligenceEventType.Token, IntelligenceEventType.Token],
            events.Select(static evt => evt.Type));
        Assert.Equal(["upper", "tail"], events.Select(static evt => evt.Data));
        Assert.Equal(
            5,
            logger.Messages.Count(static message =>
                message.Contains("deserialize", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_reassembles_split_multibyte_utf8_characters()
    {
        const string multibyteText = "before 😀 漢字 after";
        IntelligenceEvent token = new(
            IntelligenceEventType.Token,
            string.Empty,
            multibyteText);
        string ndjson = JsonSerializer.Serialize(
            token,
            TheForgeJsonContext.Default.IntelligenceEvent) + "\n";
        ListLogger<ArcanumApiClient> logger = new();
        ArcanumApiClient client = CreateClient(
            new NdjsonHandler(ndjson, maxChunkBytes: 1),
            logger);

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                           "/api/intelligence/ping-stream",
                           new PingRequest("hello"),
                           TheForgeJsonContext.Default.PingRequest,
                           TheForgeJsonContext.Default.IntelligenceEvent,
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        IntelligenceEvent parsed = Assert.Single(events);
        Assert.Equal(multibyteText, parsed.Data);
        Assert.DoesNotContain(
            logger.Messages,
            static message => message.Contains("deserialize", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_discards_oversize_line_and_continues()
    {
        IntelligenceEvent token = new(IntelligenceEventType.Token, string.Empty, "tail");
        string ndjson = new string('x', BoundedTextLineReader.DefaultMaxLineChars + 1)
            + "\n"
            + JsonSerializer.Serialize(token, TheForgeJsonContext.Default.IntelligenceEvent)
            + "\n";
        ListLogger<ArcanumApiClient> logger = new();
        ArcanumApiClient client = CreateClient(ndjson, logger);

        List<IntelligenceEvent> events = [];
        await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                           "/api/intelligence/ping-stream",
                           new PingRequest("hello"),
                           TheForgeJsonContext.Default.PingRequest,
                           TheForgeJsonContext.Default.IntelligenceEvent,
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        IntelligenceEvent parsed = Assert.Single(events);
        Assert.Equal("tail", parsed.Data);
        Assert.Contains(
            logger.Messages,
            static message => message.Contains("maximum line size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_NonSuccessStatusCode_ThrowsInsteadOfYieldingNothing()
    {
        ListLogger<ArcanumApiClient> logger = new();
        ArcanumApiClient client = CreateClient(new StatusCodeHandler(HttpStatusCode.Unauthorized), logger);

        async Task DrainAsync()
        {
            await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                               "/api/spells/test/execute-stream",
                               new PingRequest("hello"),
                               TheForgeJsonContext.Default.PingRequest,
                               TheForgeJsonContext.Default.IntelligenceEvent,
                               CancellationToken.None))
            {
                _ = evt;
            }
        }

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(DrainAsync);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_EmptyBaseUrl_ThrowsInsteadOfYieldingNothing()
    {
        ArcanumApiClient client = CreateClient(
            new NdjsonHandler(string.Empty),
            NullLogger<ArcanumApiClient>.Instance,
            baseUrl: string.Empty);

        async Task DrainAsync()
        {
            await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                               "/api/intelligence/ping-stream",
                               new PingRequest("hello"),
                               TheForgeJsonContext.Default.PingRequest,
                               TheForgeJsonContext.Default.IntelligenceEvent,
                               CancellationToken.None))
            {
                _ = evt;
            }
        }

        // Same class of silent failure as a non-2xx response (W11-2): a malformed BaseUrl must not
        // read as "stream completed with zero frames".
        await Assert.ThrowsAsync<HttpRequestException>(DrainAsync);
    }

    [Fact]
    public async Task PostNdjsonStreamAsync_MissingApiKey_ThrowsInsteadOfYieldingNothing()
    {
        ArcanumApiClient client = CreateClient(
            new NdjsonHandler(string.Empty),
            NullLogger<ArcanumApiClient>.Instance,
            apiKey: string.Empty);

        async Task DrainAsync()
        {
            await foreach (IntelligenceEvent evt in client.PostNdjsonStreamAsync(
                               "/api/intelligence/ping-stream",
                               new PingRequest("hello"),
                               TheForgeJsonContext.Default.PingRequest,
                               TheForgeJsonContext.Default.IntelligenceEvent,
                               CancellationToken.None))
            {
                _ = evt;
            }
        }

        await Assert.ThrowsAsync<HttpRequestException>(DrainAsync);
    }

    private static ArcanumApiClient CreateClient(
        string ndjson,
        ILogger<ArcanumApiClient> logger) =>
        CreateClient(new NdjsonHandler(ndjson), logger);

    private static ArcanumApiClient CreateClient(
        HttpMessageHandler handler,
        ILogger<ArcanumApiClient> logger,
        string baseUrl = "http://localhost:5001",
        string apiKey = "test-key") =>
        new(
            new StaticHttpClientFactory(handler),
            new StaticTheForgeSettingsMonitor(new TheForgeSettings
            {
                BaseUrl = baseUrl,
            }),
            new StaticApiKeyProvider(apiKey),
            logger);

    private sealed class StaticApiKeyProvider(string apiKey) : ITheForgeApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(apiKey);

        public Task PersistPastedKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ClearPasteDecline()
        {
        }
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001"),
            };
    }

    private sealed class NdjsonHandler(string ndjson, int? maxChunkBytes = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = maxChunkBytes is int chunkSize
                    ? new StreamContent(new FragmentingResponseStream(
                        Encoding.UTF8.GetBytes(ndjson),
                        chunkSize))
                    : new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
            return Task.FromResult(response);
        }
    }

    private sealed class StatusCodeHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(string.Empty),
            });
    }

    private sealed class FragmentingResponseStream(byte[] payload, int maxChunkBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => payload.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= payload.Length)
            {
                return 0;
            }

            int count = Math.Min(Math.Min(buffer.Length, maxChunkBytes), payload.Length - _position);
            payload.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer.Span);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
