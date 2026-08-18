using System.Collections.Concurrent;

using System.Net;

using System.Runtime.CompilerServices;

using System.Text.Json;

using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Hosting;

using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Logging;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Logging;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LogsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public LogsEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetLogs_WithValidApiKey_ReturnsLogQueryEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/logs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LogQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLogQueryResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.Entries);

    }

    [SkippableFact]
    public async Task GetLogs_WithLimit_ReturnsOkEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/logs?limit=10");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LogQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLogQueryResult);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task StreamLogs_PassesFiltersAndUsesCommentForConnectionSentinel()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CapturingLogQueryService query = new();

        await using ArcanumWebApplicationFactory factory = new()
        {

            ServiceOverrides = services =>
            {

                services.RemoveAll<ILogQueryService>();

                services.AddSingleton<ILogQueryService>(query);

            },

        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/events/logs?level=warning&category=Api&search=needle");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        LogQueryRequest request = Assert.IsType<LogQueryRequest>(query.StreamRequest);

        Assert.Equal(LogLevel.Warning, request.MinLevel);

        Assert.Equal("Api", request.Category);

        Assert.Equal("needle", request.Search);

        string body = await response.Content.ReadAsStringAsync();

        Assert.StartsWith(": connected\n\n", body, StringComparison.Ordinal);

        Assert.DoesNotContain("data: {\"connected\":true}", body, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task StreamLogs_BrokenPipeOnTheConnectedComment_IsNotAnUnhandledError()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CapturingLogQueryService query = new();

        RecordingLoggerProvider recording = new();

        await using ArcanumWebApplicationFactory factory = new()
        {

            ServiceOverrides = services =>
            {

                services.RemoveAll<ILogQueryService>();

                services.AddSingleton<ILogQueryService>(query);

                services.RemoveAll<Microsoft.Extensions.Logging.ILoggerFactory>();

                services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(
                    new Microsoft.Extensions.Logging.LoggerFactory([recording]));

                services.AddSingleton<IStartupFilter>(new BrokenPipeStartupFilter("/api/events/logs"));

            },

        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        // The client hung up before the very first byte. Every subsequent write on this route goes
        // through SseStreamWriter, which classifies a broken pipe as a disconnect — only the
        // `: connected` sentinel is written outside it, so an IOException there escapes the handler
        // and is logged as an application fault.
        _ = await client.GetAsync("/api/events/logs");

        Assert.DoesNotContain(
            recording.Entries,
            static entry => entry.Level == MsLogLevel.Error
                && entry.Message.Contains("Unhandled exception on", StringComparison.Ordinal));

    }

    /// <summary>
    /// Replaces the response body of one route with a stream whose first write fails, reproducing a
    /// client that hung up between the response headers and the first frame.
    /// </summary>
    private sealed class BrokenPipeStartupFilter(string path) : IStartupFilter
    {

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {

                _ = app.Use(async (HttpContext context, Func<Task> nextMiddleware) =>
                {

                    if (context.Request.Path.StartsWithSegments(path))
                    {

                        context.Response.Body = new BrokenPipeStream();

                    }

                    await nextMiddleware().ConfigureAwait(false);

                });

                next(app);

            };

    }

    private sealed class BrokenPipeStream : Stream
    {

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => 0;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("broken pipe");

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            new(Task.FromException(new IOException("broken pipe")));

    }

    private sealed class RecordingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {

        private readonly ConcurrentQueue<RecordedLog> _entries = new();

        public IReadOnlyCollection<RecordedLog> Entries => _entries;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new RecordingLogger(_entries);

        public void Dispose()
        {

            // Nothing to release.

        }

        private sealed class RecordingLogger(ConcurrentQueue<RecordedLog> entries) : Microsoft.Extensions.Logging.ILogger
        {

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(MsLogLevel logLevel) => true;

            public void Log<TState>(
                MsLogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new RecordedLog(logLevel, formatter(state, exception)));

        }

    }

    private sealed record RecordedLog(MsLogLevel Level, string Message);

    [SkippableTheory]
    [InlineData("verbose")]
    [InlineData("999")]
    public async Task StreamLogs_WithUnknownLevel_ReturnsValidationEnvelope(string level)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        CapturingLogQueryService query = new();

        await using ArcanumWebApplicationFactory factory = new()
        {

            ServiceOverrides = services =>
            {

                services.RemoveAll<ILogQueryService>();

                services.AddSingleton<ILogQueryService>(query);

            },

        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/events/logs?level={level}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LogQueryResult>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLogQueryResult);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.InvalidQuery, body.Error?.Code);

        Assert.Null(query.StreamRequest);

    }

    private sealed class CapturingLogQueryService : ILogQueryService
    {

        public LogQueryRequest? StreamRequest { get; private set; }

        public Task<LogQueryResult> QueryAsync(LogQueryRequest request, CancellationToken ct) =>
            Task.FromResult(new LogQueryResult([], null, false));

        public async IAsyncEnumerable<LogEntry> StreamAsync(
            LogQueryRequest? request,
            [EnumeratorCancellation] CancellationToken ct)
        {

            StreamRequest = request;

            await Task.CompletedTask;

            yield return new LogEntry(
                1,
                DateTimeOffset.Parse("2026-08-01T12:00:00Z"),
                LogLevel.Warning,
                "Api",
                "needle",
                null,
                null,
                null,
                []);

        }

    }

}
