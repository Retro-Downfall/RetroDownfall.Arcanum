using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.LlamaCpp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ArcanumApiClientTests
{

    [Fact]
    public async Task AskAsync_returns_failure_when_api_key_missing()
    {

        RecordingHandler handler = new();

        ArcanumApiClient client = CreateClient(handler, apiKey: null);

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Security.MissingApiKey", result.Error.Code);

        Assert.Empty(handler.Requests);

    }

    [Fact]
    public async Task AskAsync_posts_ping_request_with_api_key_header()
    {

        RecordingHandler handler = new(_ => CreatePromptResponse(
            new ApiResponse<PromptResponseDto>(
                new PromptResponseDto("pong", null),
                true,
                null)));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal("pong", result.Value);

        Assert.Single(handler.Requests);

        HttpRequestMessage request = handler.Requests[0];

        Assert.Equal(HttpMethod.Post, request.Method);

        Assert.Equal("/api/intelligence/ping", request.RequestUri!.AbsolutePath);

        Assert.True(request.Headers.TryGetValues(ArcanumApiHeaders.ApiKey, out IEnumerable<string>? keys));

        Assert.Equal("test-key", keys!.Single());

    }

    [Fact]
    public async Task AskAsync_returns_envelope_error_on_http_failure()
    {

        Error apiError = new("Api.RateLimited", "Too many requests.");

        RecordingHandler handler = new(_ => CreatePromptResponse(
            new ApiResponse<PromptResponseDto>(null, false, apiError),
            HttpStatusCode.TooManyRequests));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(apiError, result.Error);

    }

    [Fact]
    public async Task AskAsync_returns_invalid_response_when_body_is_not_json()
    {

        // A non-JSON body (e.g. proxy HTML on a 401/429, or a truncated response) must not
        // escape as an unhandled JsonException; it should map to Api.InvalidResponse.

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("<html>bad</html>")),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.InvalidResponse", result.Error.Code);

    }

    [Fact]
    public async Task AskAsync_returns_response_too_large_when_declared_content_length_exceeds_cap()
    {

        // A misbehaving/compromised local API declaring an oversized Content-Length must be rejected
        // before any buffering is attempted — the fake declared length here (100 MiB) exceeds
        // ArcanumApiClient's cap without this test needing to actually transfer that many bytes.
        RecordingHandler handler = new(_ =>
        {

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")),
            };

            response.Content.Headers.ContentLength = 100 * 1024 * 1024;

            return response;

        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.ResponseTooLarge", result.Error.Code);

    }

    [Fact]
    public async Task AskAsync_returns_connection_error_when_handler_throws_http_request_exception()
    {

        RecordingHandler handler = new(_ => throw new HttpRequestException("connection refused"));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Unreachable", result.Error.Code);

        Assert.Contains("unreachable", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task AskAsync_returns_disconnected_error_when_handler_throws_io_exception()
    {

        RecordingHandler handler = new(_ => throw new IOException("connection reset by peer"));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Unreachable", result.Error.Code);

        Assert.Contains("lost before the response completed", result.Error.Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task AskAsync_returns_unexpected_error_result_when_handler_throws_unanticipated_exception()
    {

        // Any exception type not explicitly mapped (OperationCanceledException, HttpRequestException,
        // IOException) must still surface as a controlled Result.Failure — never propagate past the
        // client to a command's caller / ConsoleAppFramework's generic top-level exception handler.
        RecordingHandler handler = new(_ => throw new InvalidOperationException("handler misconfigured"));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Api.UnexpectedError", result.Error.Code);

    }

    [Fact]
    public async Task AskAsync_returns_timeout_when_bounded_client_exceeds_deadline()
    {

        RecordingHandler handler = new(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            return CreatePromptResponse(
                new ApiResponse<PromptResponseDto>(
                    new PromptResponseDto("late", null),
                    true,
                    null));
        });

        ArcanumApiClient client = CreateClient(
            handler,
            apiKey: "test-key",
            requestTimeout: TimeSpan.FromMilliseconds(100));

        PingRequest body = new("hello");

        Result<string> result = await client.AskAsync(body, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal("Connection.Timeout", result.Error.Code);

    }

    [Fact]
    public async Task AskStreamAsync_yields_error_when_stream_disconnects_mid_read()
    {

        IntelligenceEvent token = new(IntelligenceEventType.Token, "partial");

        byte[] firstLine = JsonSerializer.SerializeToUtf8Bytes(token, ArcanumJsonContext.Default.IntelligenceEvent);

        DisconnectingStreamHandler handler = new(firstLine);

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PingRequest body = new("hello");

        List<IntelligenceEvent> events = [];

        await foreach (IntelligenceEvent evt in client.AskStreamAsync(body, CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Equal(2, events.Count);

        Assert.Equal(IntelligenceEventType.Token, events[0].Type);

        Assert.Equal(IntelligenceEventType.Error, events[1].Type);

        Assert.Contains("lost before the stream completed", events[1].Message, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task PullModelStreamAsync_yields_error_when_stream_disconnects_mid_read()
    {

        LlamaPullProgress progress = new() { CacheKey = "model", BytesDownloaded = 1024, Percent = 10 };

        byte[] firstLine = JsonSerializer.SerializeToUtf8Bytes(progress, ArcanumJsonContext.Default.LlamaPullProgress);

        DisconnectingStreamHandler handler = new(firstLine);

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        PullModelRequestDto body = new() { SourceUrl = "https://example.com/model.gguf" };

        List<LlamaPullProgress> frames = [];

        await foreach (LlamaPullProgress frame in client.PullModelStreamAsync(body, CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);

        Assert.Equal("model", frames[0].CacheKey);

        Assert.True(frames[1].Completed);

        Assert.Contains("lost before the stream completed", frames[1].Error, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public async Task StreamApprenticeChronicleAsync_parses_sse_frames_via_source_generated_context()
    {

        string sse =
            "data: {\"type\":\"tool_result\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"result\":\"did the thing\"}\n\n" +
            "data: {\"type\":\"status\"}\n\n" +
            "data: [DONE]\n\n";

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<ChronicleFrame> frames = [];

        await foreach (ChronicleFrame frame in client.StreamApprenticeChronicleAsync(Guid.NewGuid(), CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);

        Assert.Equal("tool_result", frames[0].Type);

        Assert.Equal("did the thing", frames[0].Message);

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), frames[0].Timestamp);

        Assert.Equal("status", frames[1].Type);

        Assert.Equal("status", frames[1].Message);

        Assert.Null(frames[1].Timestamp);

    }

    [Fact]
    public async Task StreamApprenticeChronicleAsync_skips_frames_with_malformed_json()
    {

        string sse =
            "data: not valid json\n\n" +
            "data: {\"type\":\"status\"}\n\n" +
            "data: [DONE]\n\n";

        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse),
        });

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        List<ChronicleFrame> frames = [];

        await foreach (ChronicleFrame frame in client.StreamApprenticeChronicleAsync(Guid.NewGuid(), CancellationToken.None))
        {
            frames.Add(frame);
        }

        Assert.Single(frames);

        Assert.Equal("status", frames[0].Type);

    }

    [Fact]
    public async Task SubmitHumanResponseAsync_returns_success_when_envelope_data_is_true()
    {

        RecordingHandler handler = new(_ => CreateBooleanResponse(new ApiResponse<bool>(true, true, null)));

        ArcanumApiClient client = CreateClient(handler, apiKey: "test-key");

        Result<bool> result = await client.SubmitHumanResponseAsync("prompt-1", "answer", CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value);

        Assert.Equal("/api/intelligence/human-response", handler.Requests[0].RequestUri!.AbsolutePath);

    }

    private static ArcanumApiClient CreateClient(HttpMessageHandler handler, string? apiKey)
    {

        return CreateClient(handler, apiKey, requestTimeout: null);

    }

    private static ArcanumApiClient CreateClient(
        HttpMessageHandler handler,
        string? apiKey,
        TimeSpan? requestTimeout)
    {

        FakeHttpClientFactory factory = new(handler, requestTimeout);

        FakeSecretStore secretStore = new() { ApiKey = apiKey };

        return new ArcanumApiClient(factory, secretStore);

    }

    private static HttpResponseMessage CreatePromptResponse(ApiResponse<PromptResponseDto> envelope, HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponsePromptResponseDto);

        HttpResponseMessage response = new(status)
        {
            Content = new ByteArrayContent(json),
        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return response;

    }

    private static HttpResponseMessage CreateBooleanResponse(ApiResponse<bool> envelope, HttpStatusCode status = HttpStatusCode.OK)
    {

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, ArcanumJsonContext.Default.ApiResponseBoolean);

        HttpResponseMessage response = new(status)
        {
            Content = new ByteArrayContent(json),
        };

        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        return response;

    }

    private sealed class FakeSecretStore : ISecretStore
    {

        public string? ApiKey { get; set; }

    public Task<string?> GetApiKeyAsync() => Task.FromResult(ApiKey);

    public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(ApiKey)
                ? SecretStoreReadResult.Missing()
                : SecretStoreReadResult.Ok(ApiKey!));

    public Task SaveApiKeyAsync(string apiKey) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() => Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) => Task.CompletedTask;

    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler, TimeSpan? requestTimeout) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name)
        {

            HttpClient client = new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost:5001/"),
            };

            if (string.Equals(name, ArcanumApiClient.RequestHttpClientName, StringComparison.Ordinal))
            {
                client.Timeout = requestTimeout ?? TimeSpan.FromSeconds(60);
            }
            else
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
            }

            return client;

        }

    }

    private sealed class RecordingHandler : HttpMessageHandler
    {

        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
        {
            _responder = responder is null
                ? (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : (request, _) => Task.FromResult(responder(request));
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage snapshot = new(request.Method, request.RequestUri)
            {
                Content = request.Content,
            };

            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                snapshot.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            Requests.Add(snapshot);

            return await _responder(request, cancellationToken).ConfigureAwait(false);

        }

    }

    private sealed class DisconnectingStreamHandler(byte[] firstLineBytes) : HttpMessageHandler
    {

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            byte[] payload = new byte[firstLineBytes.Length + 1];

            firstLineBytes.CopyTo(payload, 0);

            payload[^1] = (byte)'\n';

            DisconnectingResponseStream stream = new(payload);

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream),
            };

            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");

            return Task.FromResult(response);

        }

    }

    private sealed class DisconnectingResponseStream(byte[] firstChunk) : Stream
    {

        private bool _firstChunkDelivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {

            throw new NotSupportedException();

        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(Span<byte> buffer)
        {

            if (!_firstChunkDelivered)
            {
                int copied = Math.Min(buffer.Length, firstChunk.Length);

                firstChunk.AsSpan(0, copied).CopyTo(buffer);

                _firstChunkDelivered = true;

                return copied;
            }

            throw new IOException("Simulated mid-stream disconnect.");

        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            await Task.Yield();

            return Read(buffer.Span);

        }

    }

}
