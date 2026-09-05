using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Middleware;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Api.Middleware;

public sealed class ArcanumExceptionHandlerTests
{

    [Fact]
    public async Task TryHandleAsync_V1Path_WithResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_JsonException_V1Path_ReturnsOpenAiInvalidJson()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(400, httpContext.Response.StatusCode);

    }

    [Fact]
    public async Task TryHandleAsync_JsonException_ResponseStarted_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.False(handled);

    }

    [Fact]
    public async Task TryHandleAsync_JsonException_NonV1Path_ReturnsInvalidBody()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new JsonException("bad json"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(400, httpContext.Response.StatusCode);

    }

    [Fact]
    public async Task TryHandleAsync_NonJsonException_V1Path_ReturnsOpenAiUnhandledError()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/models";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

    }

    [Fact]
    public async Task TryHandleAsync_NonJsonException_NonV1Path_ReturnsInternalError()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(500, httpContext.Response.StatusCode);

    }

    [Fact]
    public async Task TryHandleAsync_RequestAbortedOperationCanceled_ReturnsFalse()
    {
        ArcanumExceptionHandler handler = new(NullLogger<ArcanumExceptionHandler>.Instance);

        using CancellationTokenSource cts = new();

        cts.Cancel();

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.RequestAborted = cts.Token;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new OperationCanceledException(),
            CancellationToken.None);

        Assert.False(handled);

    }

    /// <summary>
    /// A gate that closed under an in-flight request answers exactly as admission would have.
    /// </summary>
    /// <remarks>
    /// Admission refuses what arrives after stage one begins. A request admitted a moment earlier is
    /// drained, and while it drains it can still reach SQLite and be refused there — so without this
    /// arm the one window the refusal exists for produces a 500 instead, and the request path is
    /// written into an Error-level log on the way past.
    /// </remarks>
    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_ApiPath_IsTheDocumentedServiceUnavailable()
    {

        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/sessions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.Equal(ErrorCodes.Grimoire.MaintenanceUnavailable, body.Error?.Code);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.DoesNotContain(
            logger.Entries,
            static entry => entry.Message.Contains("/api/sessions", StringComparison.Ordinal));

        Assert.All(logger.Entries, static entry => Assert.Null(entry.Exception));

    }

    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_V1Path_IsTheOpenAiServiceUnavailable()
    {

        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/v1/chat/completions";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, httpContext.Response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            ReadBody(httpContext),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("service_unavailable", body.Error.Type);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

    }

    [Fact]
    public async Task TryHandleAsync_MaintenanceRefusal_WithResponseStarted_RewritesNothing()
    {

        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext(responseStarted: true);

        httpContext.Request.Path = "/api/events/logs";

        httpContext.Response.StatusCode = StatusCodes.Status200OK;

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new GrimoireMaintenanceUnavailableException(),
            CancellationToken.None);

        Assert.False(handled);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);

        Assert.DoesNotContain(logger.Entries, static entry => entry.Level == LogLevel.Error);

    }

    /// <summary>
    /// Every other exception keeps its Error-level line and its 500.
    /// </summary>
    [Fact]
    public async Task TryHandleAsync_UnexpectedException_StillLogsAtError()
    {

        RecordingLogger logger = new();

        ArcanumExceptionHandler handler = new(logger);

        DefaultHttpContext httpContext = CreateHttpContext();

        httpContext.Request.Path = "/api/spells/execute";

        bool handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        Assert.True(handled);

        Assert.Equal(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);

        Assert.Contains(logger.Entries, static entry => entry.Level == LogLevel.Error);

    }

    private static string ReadBody(HttpContext httpContext)
    {

        MemoryStream body = (MemoryStream)httpContext.Features
            .GetRequiredFeature<IHttpResponseBodyFeature>()
            .Stream;

        return Encoding.UTF8.GetString(body.ToArray());

    }

    private sealed class RecordingLogger : ILogger<ArcanumExceptionHandler>
    {

        private readonly List<LogEntry> _entries = [];

        internal IReadOnlyList<LogEntry> Entries
        {

            get
            {

                lock (_entries)
                {

                    return [.. _entries];

                }

            }

        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {

            lock (_entries)
            {

                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));

            }

        }

    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private static DefaultHttpContext CreateHttpContext(bool responseStarted = false)
    {
        ServiceCollection services = new();

        services.AddRouting();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        TestResponseFeature responseFeature = new()
        {
            HasStarted = responseStarted,
            Body = body,
        };

        TestResponseBodyFeature bodyFeature = new(body, responseFeature);

        TestRequestFeature requestFeature = new();

        FeatureCollection features = new();

        features.Set<IHttpRequestFeature>(requestFeature);

        features.Set<IHttpResponseFeature>(responseFeature);

        features.Set<IHttpResponseBodyFeature>(bodyFeature);

        DefaultHttpContext httpContext = new(features);

        httpContext.RequestServices = provider;

        return httpContext;
    }

    private sealed class TestRequestFeature : IHttpRequestFeature
    {

        public string Protocol { get; set; } = "HTTP/1.1";

        public string Scheme { get; set; } = "http";

        public string Method { get; set; } = "POST";

        public string PathBase { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string QueryString { get; set; } = string.Empty;

        public string RawTarget { get; set; } = string.Empty;

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

    }

    private sealed class TestResponseFeature : IHttpResponseFeature
    {

        public int StatusCode { get; set; } = 200;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = new MemoryStream();

        public bool HasStarted { get; set; }

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }

    }

    private sealed class TestResponseBodyFeature : IHttpResponseBodyFeature
    {

        private readonly Stream _stream;

        private readonly IHttpResponseFeature _responseFeature;

        private readonly PipeWriter _writer;

        public TestResponseBodyFeature(Stream stream, IHttpResponseFeature responseFeature)
        {

            _stream = stream;

            _responseFeature = responseFeature;

            _writer = PipeWriter.Create(stream);

        }

        public Stream Stream => _stream;

        // The same stream the feature reports, so a result that writes through BodyWriter — which
        // every Results.Json does — lands where a test that reads the body can see it.
        public PipeWriter Writer => _writer;

        public Task CompleteAsync() => Task.CompletedTask;

        public void DisableBuffering()
        {
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {

            return Task.CompletedTask;

        }

    }

}
