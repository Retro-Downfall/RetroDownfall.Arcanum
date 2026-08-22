using System.Net;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Models;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Services;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class WatchSseTransportTests
{

    /// <summary>
    /// Hang guard for every await in this file, not a behavioural assertion: the transport is driven
    /// deterministically by <c>GatedStreamContent</c> / <c>ControlledDelay</c>, so these timeouts only
    /// exist to fail a genuine deadlock instead of hanging the suite. One second was tight enough that
    /// a loaded machine tripped them, which reads as a transport bug and is not one.
    /// </summary>
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);


    [Fact]

    public async Task WatchSseAsync_authenticates_and_preserves_multiline_json()
    {

        const string RawJson = "{\"message\":\n\"hello\"}";

        RecordingHandler handler = new(
            _ => Sse(
                ": keep-alive\n\n"
                + "event: log\n"
                + "data: {\"message\":\n"
                + "data: \"hello\"}\n\n"
                + "data: {\"type\":\"live\"}\n\n"
                + "data: [DONE]\n\n"
                + "data: {\"ignored\":true}\n\n"));

        ArcanumApiClient client = CreateClient(handler, "test-key");

        List<WatchSseFrame> frames = await CollectAsync(
            client.WatchSseAsync("/api/events/logs", CancellationToken.None));

        Assert.Collection(
            frames,
            frame =>
            {

                Assert.Equal(WatchSseFrameType.Heartbeat, frame.Type);

                Assert.Contains("keep-alive", frame.Diagnostic!, StringComparison.OrdinalIgnoreCase);

            },
            frame =>
            {

                Assert.Equal(WatchSseFrameType.Data, frame.Type);

                Assert.Equal(RawJson, frame.RawJson);

                Assert.Equal("hello", frame.Data!.Value.GetProperty("message").GetString());

                Assert.DoesNotContain('\n', frame.Data.Value.GetRawText());

            },
            frame =>
            {

                Assert.Equal(WatchSseFrameType.Heartbeat, frame.Type);

                Assert.Contains("live", frame.Diagnostic!, StringComparison.OrdinalIgnoreCase);

            },
            frame => Assert.Equal(WatchSseFrameType.Done, frame.Type));

        HttpRequestMessage request = Assert.Single(handler.Requests);

        Assert.Equal("/api/events/logs", request.RequestUri!.AbsolutePath);

        Assert.True(request.Headers.TryGetValues(ArcanumApiHeaders.ApiKey, out IEnumerable<string>? keys));

        Assert.Equal("test-key", Assert.Single(keys!));

        Assert.Contains(
            request.Headers.Accept,
            value => string.Equals(
                value.MediaType,
                "text/event-stream",
                StringComparison.Ordinal));

    }

    [Fact]

    public async Task WatchSseAsync_rejects_an_absolute_uri_before_sending_authentication()
    {

        RecordingHandler handler = new(
            _ => Sse("data: [DONE]\n\n"));

        ArcanumApiClient client = CreateClient(handler, "test-key");

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => CollectAsync(
                client.WatchSseAsync(
                    "https://example.invalid/collect",
                    CancellationToken.None)));

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public async Task WatchSseAsync_reports_malformed_json_and_continues_at_the_next_event()
    {

        RecordingHandler handler = new(
            _ => Sse(
                "data: not-json\n\n"
                + "data: {\"sequence\":7}\n\n"
                + "data: [DONE]\n\n"));

        ArcanumApiClient client = CreateClient(handler, "test-key");

        List<WatchSseFrame> frames = await CollectAsync(
            client.WatchSseAsync("api/events/logs", CancellationToken.None));

        Assert.Collection(
            frames,
            frame =>
            {

                Assert.Equal(WatchSseFrameType.Error, frame.Type);

                Assert.Equal("Api.InvalidResponse", frame.Error!.Value.Code);

                Assert.DoesNotContain("not-json", frame.Error.Value.Message, StringComparison.Ordinal);

                Assert.True(frame.Recoverable);

                Assert.False(frame.Retryable);

            },
            frame =>
            {

                Assert.Equal(WatchSseFrameType.Data, frame.Type);

                Assert.Equal(7, frame.Data!.Value.GetProperty("sequence").GetInt32());

            },
            frame => Assert.Equal(WatchSseFrameType.Done, frame.Type));

    }

    [Fact]

    public async Task WatchSseAsync_distinguishes_clean_eof_without_done()
    {

        RecordingHandler handler = new(
            _ => Sse("data: {\"sequence\":7}\n\n"));

        ArcanumApiClient client = CreateClient(handler, "test-key");

        List<WatchSseFrame> frames = await CollectAsync(
            client.WatchSseAsync("api/events/logs", CancellationToken.None));

        Assert.Collection(
            frames,
            frame => Assert.Equal(WatchSseFrameType.Data, frame.Type),
            frame =>
            {

                Assert.Equal(WatchSseFrameType.UnexpectedEof, frame.Type);

                Assert.Contains("disconnect", frame.Diagnostic!, StringComparison.OrdinalIgnoreCase);

                Assert.True(frame.Retryable);

            });

    }

    [Fact]

    public async Task WatchSseParser_emits_nonfatal_idle_diagnostic_then_continues()
    {

        GatedTextReader reader = new();

        ControlledDelay delay = new();

        await using IAsyncEnumerator<WatchSseFrame> frames = WatchSseParser
            .ParseAsync(
                reader,
                TimeSpan.FromMinutes(1),
                delay.WaitAsync,
                CancellationToken.None)
            .GetAsyncEnumerator();

        Task<bool> idleMove = frames.MoveNextAsync().AsTask();

        await reader.ReadStarted.Task.WaitAsync(AsyncTestTimeout);

        delay.Release();

        Assert.True(await idleMove.WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Heartbeat, frames.Current.Type);

        reader.Release("data: {\"sequence\":8}\r\rdata: [DONE]\r\r");

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Data, frames.Current.Type);

        Assert.Equal(8, frames.Current.Data!.Value.GetProperty("sequence").GetInt32());

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Done, frames.Current.Type);

        Assert.False(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

    }

    [Fact]

    public async Task WatchSseParser_surfaces_keepalive_comments_without_emitting_data()
    {

        using StringReader reader = new(
            ": keep-alive\n\n"
            + ": heartbeat\n\n"
            + "data: [DONE]\n\n");

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(reader, CancellationToken.None));

        Assert.Collection(
            frames,
            first =>
            {

                Assert.Equal(WatchSseFrameType.Heartbeat, first.Type);

                Assert.Null(first.Data);

                Assert.Contains("waiting", first.Diagnostic!, StringComparison.OrdinalIgnoreCase);

            },
            second => Assert.Equal(WatchSseFrameType.Heartbeat, second.Type),
            done => Assert.Equal(WatchSseFrameType.Done, done.Type));

    }

    [Fact]

    public async Task WatchSseParser_accepts_done_at_eof_without_a_blank_line()
    {

        using StringReader reader = new("data: [DONE]");

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(reader, CancellationToken.None));

        WatchSseFrame frame = Assert.Single(frames);

        Assert.Equal(WatchSseFrameType.Done, frame.Type);

    }

    [Fact]

    public async Task WatchSseParser_does_not_impose_a_client_only_payload_cap()
    {

        const int FormerClientLineLimit = 1024 * 1024;

        string content = new('x', FormerClientLineLimit + 1);

        using StringReader reader = new(
            $"data: {{\"message\":\"{content}\"}}\n\n"
            + "data: [DONE]\n\n");

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(reader, CancellationToken.None));

        Assert.Collection(
            frames,
            frame =>
            {

                Assert.Equal(WatchSseFrameType.Data, frame.Type);

                Assert.Equal(
                    content.Length,
                    frame.Data!.Value.GetProperty("message").GetString()!.Length);

            },
            frame => Assert.Equal(WatchSseFrameType.Done, frame.Type));

    }

    [Fact]

    public async Task WatchSseParser_does_not_hide_a_data_event_with_connected_metadata()
    {

        using StringReader reader = new(
            "data: {\"connected\":true,\"sequence\":10}\n\n"
            + "data: [DONE]\n\n");

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(reader, CancellationToken.None));

        Assert.Collection(
            frames,
            frame => Assert.Equal(WatchSseFrameType.Data, frame.Type),
            frame => Assert.Equal(WatchSseFrameType.Done, frame.Type));

    }

    [Fact]

    public async Task WatchSseAsync_does_not_echo_an_unstructured_http_error_body()
    {

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {

                Content = new StringContent("proxy-secret-stack-trace"),

            });

        ArcanumApiClient client = CreateClient(handler, "test-key");

        WatchSseFrame frame = Assert.Single(
            await CollectAsync(client.WatchSseAsync("api/events/mcp", CancellationToken.None)));

        Assert.Equal(WatchSseFrameType.Error, frame.Type);

        Assert.Equal("Api.HttpError", frame.Error!.Value.Code);

        Assert.DoesNotContain("proxy-secret", frame.Error.Value.Message, StringComparison.Ordinal);

        Assert.Contains("502", frame.Error.Value.Message, StringComparison.Ordinal);

        Assert.True(frame.Retryable);

    }

    [Theory]

    [InlineData(400, false)]

    [InlineData(401, false)]

    [InlineData(403, false)]

    [InlineData(404, false)]

    [InlineData(408, true)]

    [InlineData(425, true)]

    [InlineData(429, true)]

    [InlineData(500, true)]

    [InlineData(503, true)]

    public async Task WatchSseAsync_classifies_http_retryability(
        int statusCode,
        bool expectedRetryable)
    {

        RecordingHandler handler = new(
            _ => new HttpResponseMessage((HttpStatusCode)statusCode));

        ArcanumApiClient client = CreateClient(handler, "test-key");

        WatchSseFrame frame = Assert.Single(
            await CollectAsync(client.WatchSseAsync("api/events/mcp", CancellationToken.None)));

        Assert.Equal(WatchSseFrameType.Error, frame.Type);

        Assert.Equal(expectedRetryable, frame.Retryable);

    }

    [Fact]

    public async Task WatchSseAsync_marks_network_failures_retryable()
    {

        RecordingHandler handler = new(
            _ => throw new HttpRequestException("Connection refused."));

        ArcanumApiClient client = CreateClient(handler, "test-key");

        WatchSseFrame frame = Assert.Single(
            await CollectAsync(client.WatchSseAsync("api/events/mcp", CancellationToken.None)));

        Assert.Equal(WatchSseFrameType.Error, frame.Type);

        Assert.True(frame.Retryable);

    }

    [Fact]

    public async Task WatchSseAsync_does_not_retry_an_sse_connection_cap_denial()
    {

        Error capError = new(
            ErrorCodes.Api.TooManyConnections,
            "The server has reached the maximum number of concurrent SSE connections.");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            ApiResponse<bool>.FromResult(Result<bool>.Failure(capError)),
            ArcanumJsonContext.Default.ApiResponseBoolean);

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {

                Content = new ByteArrayContent(json),

            });

        ArcanumApiClient client = CreateClient(handler, "test-key");

        WatchSseFrame frame = Assert.Single(
            await CollectAsync(
                client.WatchSseAsync(
                    "api/events/mcp",
                    CancellationToken.None)));

        Assert.Equal(capError, frame.Error);

        Assert.False(frame.Retryable);

    }

    [Fact]

    public async Task WatchSseAsync_reports_waiting_for_response_headers_without_timing_out()
    {

        GatedResponseHandler handler = new(Sse("data: [DONE]\n\n"));

        ControlledDelay delay = new();

        ArcanumApiClient client = CreateClient(handler, "test-key");

        await using IAsyncEnumerator<WatchSseFrame> frames = client
            .WatchSseAsync(
                "api/events/mcp",
                TimeSpan.FromMinutes(1),
                delay.WaitAsync,
                CancellationToken.None)
            .GetAsyncEnumerator();

        Task<bool> waitingMove = frames.MoveNextAsync().AsTask();

        await handler.Started.Task.WaitAsync(AsyncTestTimeout);

        delay.Release();

        Assert.True(await waitingMove.WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Heartbeat, frames.Current.Type);

        Assert.Contains("headers", frames.Current.Diagnostic!, StringComparison.OrdinalIgnoreCase);

        handler.Release();

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Done, frames.Current.Type);

        Assert.False(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

    }

    [Fact]

    public async Task WatchSseAsync_reports_waiting_for_response_stream_without_timing_out()
    {

        GatedStreamContent content = new();

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {

                Content = content,

            });

        ControlledDelay delay = new();

        ArcanumApiClient client = CreateClient(handler, "test-key");

        await using IAsyncEnumerator<WatchSseFrame> frames = client
            .WatchSseAsync(
                "api/events/mcp",
                TimeSpan.FromMinutes(1),
                delay.WaitAsync,
                CancellationToken.None)
            .GetAsyncEnumerator();

        Task<bool> waitingMove = frames.MoveNextAsync().AsTask();

        await content.OpenStarted.Task.WaitAsync(AsyncTestTimeout);

        delay.Release();

        Assert.True(await waitingMove.WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Heartbeat, frames.Current.Type);

        Assert.Contains("stream", frames.Current.Diagnostic!, StringComparison.OrdinalIgnoreCase);

        content.Release("data: [DONE]\n\n");

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Done, frames.Current.Type);

        Assert.False(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

    }

    [Fact]

    public async Task WatchSseAsync_reports_waiting_for_error_details_without_timing_out()
    {

        GatedStreamContent content = new();

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {

                Content = content,

            });

        ControlledDelay delay = new();

        ArcanumApiClient client = CreateClient(handler, "test-key");

        await using IAsyncEnumerator<WatchSseFrame> frames = client
            .WatchSseAsync(
                "api/events/mcp",
                TimeSpan.FromMinutes(1),
                delay.WaitAsync,
                CancellationToken.None)
            .GetAsyncEnumerator();

        Task<bool> waitingMove = frames.MoveNextAsync().AsTask();

        await content.OpenStarted.Task.WaitAsync(AsyncTestTimeout);

        delay.Release();

        Assert.True(await waitingMove.WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Heartbeat, frames.Current.Type);

        Assert.Contains("error", frames.Current.Diagnostic!, StringComparison.OrdinalIgnoreCase);

        content.Release("{}");

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Error, frames.Current.Type);

        Assert.True(frames.Current.Retryable);

        Assert.False(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

    }

    [Fact]

    public async Task WatchSseAsync_fails_without_sending_when_api_key_is_missing()
    {

        RecordingHandler handler = new(_ => Sse("data: [DONE]\n\n"));

        ArcanumApiClient client = CreateClient(handler, apiKey: null);

        WatchSseFrame frame = Assert.Single(
            await CollectAsync(client.WatchSseAsync("api/events/mcp", CancellationToken.None)));

        Assert.Equal(WatchSseFrameType.Error, frame.Type);

        Assert.Equal("Security.MissingApiKey", frame.Error!.Value.Code);

        Assert.False(frame.Recoverable);

        Assert.False(frame.Retryable);

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public async Task WatchSseAsync_propagates_requested_cancellation()
    {

        BlockingHandler handler = new();

        ArcanumApiClient client = CreateClient(handler, "test-key");

        using CancellationTokenSource cancellation = new();

        await using IAsyncEnumerator<WatchSseFrame> frames = client
            .WatchSseAsync("api/events/mcp", cancellation.Token)
            .GetAsyncEnumerator();

        Task<bool> move = frames.MoveNextAsync().AsTask();

        await handler.Started.Task.WaitAsync(AsyncTestTimeout);

        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.WaitAsync(AsyncTestTimeout));

    }

    [Fact]

    public async Task WatchSseAsync_cancels_while_waiting_for_the_api_key()
    {

        RecordingHandler handler = new(
            _ => Sse("data: [DONE]\n\n"));

        BlockingSecretStore secrets = new();

        ArcanumApiClient client = new(
            new FakeHttpClientFactory(handler),
            secrets);

        using CancellationTokenSource cancellation = new();

        IAsyncEnumerator<WatchSseFrame> frames = client
            .WatchSseAsync("api/events/mcp", cancellation.Token)
            .GetAsyncEnumerator();

        Task<bool>? move = null;

        try
        {

            move = frames.MoveNextAsync().AsTask();

            await secrets.Started.Task.WaitAsync(AsyncTestTimeout);

            await cancellation.CancelAsync();

            Task completed = await Task.WhenAny(
                move,
                Task.Delay(AsyncTestTimeout));

            Assert.Same(move, completed);

            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => move);

        }
        finally
        {

            secrets.Release();

            if (move is not null)
            {

                try
                {

                    _ = await move.WaitAsync(AsyncTestTimeout);

                }
                catch (Exception)
                {

                }

            }

            await frames.DisposeAsync();

        }

        Assert.Empty(handler.Requests);

    }

    [Fact]

    public async Task GetHealthReportAsync_accepts_valid_unhealthy_503_envelope()
    {

        HealthReportDto report = new(
            HealthStatus.Unhealthy,
            [new HealthComponentDto("Grimoire", HealthStatus.Unhealthy, "Unavailable")]);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<HealthReportDto>(report, true, null),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {

                Content = new ByteArrayContent(json),

            });

        ArcanumApiClient client = CreateClient(handler, "test-key");

        Result<HealthReportDto> result = await client.GetHealthReportAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(HealthStatus.Unhealthy, result.Value.Status);

        Assert.Equal("/api/health", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);

    }

    [Fact]

    public async Task GetHealthReportAsync_preserves_a_structured_503_failure()
    {

        Error apiError = new("Health.Unavailable", "Health could not be evaluated.");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<HealthReportDto>(null, false, apiError),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        RecordingHandler handler = new(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {

                Content = new ByteArrayContent(json),

            });

        ArcanumApiClient client = CreateClient(handler, "test-key");

        Result<HealthReportDto> result = await client
            .GetHealthReportAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(apiError, result.Error);

    }

    [Theory]

    [InlineData(429)]

    [InlineData(503)]

    public async Task GetHealthWatchReportAsync_marks_structured_transient_status_retryable(
        int statusCode)
    {

        Error apiError = new(
            ErrorCodes.RateLimit.TooManyRequests,
            "Health observation is temporarily unavailable.");

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            new ApiResponse<HealthReportDto>(null, false, apiError),
            ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        RecordingHandler handler = new(
            _ => new HttpResponseMessage((HttpStatusCode)statusCode)
            {

                Content = new ByteArrayContent(json),

            });

        ArcanumApiClient client = CreateClient(handler, "test-key");

        HealthWatchResult observation = await client
            .GetHealthWatchReportAsync(CancellationToken.None);

        Assert.Equal(apiError, observation.Report.Error);

        Assert.True(observation.Retryable);

    }

    private static async Task<List<WatchSseFrame>> CollectAsync(
        IAsyncEnumerable<WatchSseFrame> source)
    {

        List<WatchSseFrame> frames = [];

        await foreach (WatchSseFrame frame in source)
        {

            frames.Add(frame);

        }

        return frames;

    }

    private static ArcanumApiClient CreateClient(
        HttpMessageHandler handler,
        string? apiKey) =>
        new(
            new FakeHttpClientFactory(handler),
            new FakeSecretStore(apiKey));

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK)
        {

            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),

        };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {

        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {

                BaseAddress = new Uri("http://localhost:5001/"),

                Timeout = Timeout.InfiniteTimeSpan,

            };

    }

    private sealed class FakeSecretStore(string? apiKey) : ISecretStore
    {

        public Task<string?> GetApiKeyAsync() => Task.FromResult(apiKey);

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(
                apiKey is null
                    ? SecretStoreReadResult.Missing()
                    : SecretStoreReadResult.Ok(apiKey));

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            HttpRequestMessage recorded = new(request.Method, request.RequestUri);

            foreach ((string key, IEnumerable<string> values) in request.Headers)
            {

                _ = recorded.Headers.TryAddWithoutValidation(key, values);

            }

            Requests.Add(recorded);

            return Task.FromResult(responder(request));

        }

    }

    private sealed class BlockingSecretStore : ISecretStore
    {

        private readonly TaskCompletionSource<string?> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult("test-key");

        public async Task<string?> GetApiKeyAsync()
        {

            Started.TrySetResult();

            return await _release.Task.ConfigureAwait(false);

        }

        public Task<SecretStoreReadResult> GetApiKeyReadResultAsync() =>
            Task.FromResult(SecretStoreReadResult.Missing());

        public async Task<SecretStoreReadResult> PeekApiKeyReadResultAsync()
        {

            Started.TrySetResult();

            string? apiKey = await _release.Task.ConfigureAwait(false);

            return apiKey is null
                ? SecretStoreReadResult.Missing()
                : SecretStoreReadResult.Ok(apiKey);

        }

        public Task SaveApiKeyAsync(string key) => Task.CompletedTask;

        public Task<string?> GetGrimoireEncryptionSecretAsync() =>
            Task.FromResult<string?>(null);

        public Task SaveGrimoireEncryptionSecretAsync(string encryptionSecret) =>
            Task.CompletedTask;

    }

    private sealed class GatedTextReader : TextReader
    {

        private readonly TaskCompletionSource<string> _content = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private string? _releasedContent;

        private int _offset;

        private StringReader? _lineReader;

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(string content) => _content.TrySetResult(content);

        public override async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken)
        {

            ReadStarted.TrySetResult();

            _releasedContent ??= await _content.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            _lineReader ??= new StringReader(_releasedContent);

            return _lineReader.ReadLine();

        }

        public override async ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {

            ReadStarted.TrySetResult();

            _releasedContent ??= await _content.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (_offset >= _releasedContent.Length)
            {

                return 0;

            }

            int count = Math.Min(
                buffer.Length,
                _releasedContent.Length - _offset);

            _releasedContent.AsMemory(_offset, count).CopyTo(buffer);

            _offset += count;

            return count;

        }

    }

    private sealed class BlockingHandler : HttpMessageHandler
    {

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Started.TrySetResult();

            await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken)
                .ConfigureAwait(false);

            throw new InvalidOperationException(
                "The infinite wait unexpectedly completed.");

        }

    }

    private sealed class GatedResponseHandler(
        HttpResponseMessage response) : HttpMessageHandler
    {

        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {

            Started.TrySetResult();

            await _release.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return response;

        }

    }

    private sealed class GatedStreamContent : HttpContent
    {

        private readonly TaskCompletionSource<Stream> _stream = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OpenStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(string content) =>
            _stream.TrySetResult(
                new MemoryStream(Encoding.UTF8.GetBytes(content)));

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken)
        {

            OpenStarted.TrySetResult();

            return _stream.Task.WaitAsync(cancellationToken);

        }

        protected override bool TryComputeLength(out long length)
        {

            length = 0;

            return false;

        }

    }

    private sealed class ControlledDelay
    {

        private readonly TaskCompletionSource _first = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        public void Release() => _first.TrySetResult();

        public Task WaitAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {

            if (Interlocked.Increment(ref _calls) == 1)
            {

                return _first.Task.WaitAsync(cancellationToken);

            }

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        }

    }

}
