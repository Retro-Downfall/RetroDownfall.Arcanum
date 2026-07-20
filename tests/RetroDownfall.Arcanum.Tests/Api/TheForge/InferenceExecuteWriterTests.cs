using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

public sealed class InferenceExecuteWriterTests
{

    [Fact]
    public async Task WriteStreamAsync_StreamExceptionDuringErrorFrameWrite_DoesNotPropagate()
    {
        ServiceCollection services = new();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        ThrowingStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.RequestServices = provider;

        httpContext.Response.Body = body;

        CancellationTokenSource cts = new();

        httpContext.RequestAborted = cts.Token;

        FakeIntelligenceProvider intelligence = new();

        intelligence.NextStreamException = new InvalidOperationException("stream boom");

        body.ThrowOnNextWrite = true;

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteStreamAsync(httpContext, intelligence, request, cts.Token);

        Assert.True(body.WritesAttempted > 0);
    }

    [Fact]
    public async Task WriteStreamAsync_NonOperationCanceledException_WritesSanitizedErrorFrameAndLogsException()
    {
        ServiceCollection services = new();

        RecordingLoggerProvider recording = new();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(recording);
            builder.SetMinimumLevel(LogLevel.Trace);
        });

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.RequestServices = provider;

        httpContext.Response.Body = body;

        CancellationTokenSource cts = new();

        httpContext.RequestAborted = cts.Token;

        FakeIntelligenceProvider intelligence = new();

        intelligence.NextStreamException = new InvalidOperationException("stream boom");

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteStreamAsync(httpContext, intelligence, request, cts.Token);

        string output = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stream boom", output, StringComparison.Ordinal);
        Assert.Contains(InferenceExecuteWriter.PublicStreamFailureMessage, output, StringComparison.Ordinal);

        Assert.Contains(
            recording.Entries,
            e => e.Exception is InvalidOperationException { Message: "stream boom" }
                && e.Level >= LogLevel.Error);
    }

    // W3.4 Group A (S10): a client disconnect mid-stream must (a) cancel the linked inference
    // CTS so the producer stops promptly, (b) not write an error event to a dead socket,
    // and (c) not propagate an unhandled exception. The ThrowingStream raises IOException
    // (broken pipe) on the first token write. With the fix, the disconnect catch cancels
    // streamCts (observed via the fake's cancellation registration) and breaks silently —
    // no error frame is written and the call returns normally.
    [Fact]
    public async Task WriteStreamAsync_ClientDisconnectMidStream_CancelsInferenceAndDoesNotWriteErrorFrame()
    {

        ServiceCollection services = new();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        ThrowingStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.RequestServices = provider;

        httpContext.Response.Body = body;

        CancellationTokenSource cts = new();

        httpContext.RequestAborted = cts.Token;

        FakeIntelligenceProvider intelligence = new();

        body.ThrowOnNextWrite = true;

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteStreamAsync(httpContext, intelligence, request, cts.Token);

        Assert.True(intelligence.StreamCancellationObserved, "Inference CTS was not cancelled after client disconnect.");

        Assert.True(body.WritesAttempted > 0);

        Assert.Empty(body.CapturedWrittenText);

    }

    // Mid-stream exceptions must still emit a terminal Error frame when the client is writable
    // (native NDJSON wire contract), even after partial output has been streamed.
    [Fact]
    public async Task WriteStreamAsync_LateStreamExceptionAfterStart_WritesTerminalErrorFrame()
    {

        ServiceCollection services = new();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.RequestServices = provider;

        httpContext.Response.Body = body;

        CancellationTokenSource cts = new();

        httpContext.RequestAborted = cts.Token;

        FakeIntelligenceProvider intelligence = new();

        intelligence.ThrowOnSecondYield = new InvalidOperationException("late boom");

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteStreamAsync(httpContext, intelligence, request, cts.Token);

        string output = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("late boom", output, StringComparison.Ordinal);
        Assert.Contains(InferenceExecuteWriter.PublicStreamFailureMessage, output, StringComparison.Ordinal);

    }

    [Fact]
    public async Task WriteBufferedAsync_ProviderFails_WritesErrorResponse()
    {
        ServiceCollection services = new();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.RequestServices = provider;

        httpContext.Response.Body = body;

        FakeIntelligenceProvider intelligence = new();

        intelligence.NextFailure = new Error("Intelligence.Failed", "provider failed");

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteBufferedAsync(httpContext, intelligence, request, CancellationToken.None);

        Assert.True(httpContext.Response.StatusCode >= 400);

    }

    [Fact]
    public async Task WriteBufferedAsync_ProviderSucceeds_WritesSuccessResponse()
    {
        ServiceCollection services = new();

        services.AddLogging();

        ServiceProvider provider = services.BuildServiceProvider();

        MemoryStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.RequestServices = provider;

        httpContext.Response.Body = body;

        FakeIntelligenceProvider intelligence = new();

        intelligence.NextText = "expected output";

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteBufferedAsync(httpContext, intelligence, request, CancellationToken.None);

        Assert.Equal(200, httpContext.Response.StatusCode);

    }

    private sealed class ThrowingStream : Stream
    {

        // One-shot: the next write throws IOException, then the flag auto-resets so any
        // subsequent write (e.g. an error frame written by the general catch) is captured
        // into CapturedWrittenText. This lets a disconnect test distinguish "the disconnect
        // catch suppressed the error frame" (empty captures) from "the general catch wrote
        // an error frame to a dead socket" (non-empty captures).
        public bool ThrowOnNextWrite { get; set; }

        public int WritesAttempted { get; private set; }

        public List<string> CapturedWrittenText { get; } = new();

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

        public override void Write(byte[] buffer, int offset, int count)
        {

            WritesAttempted++;

            if (ThrowOnNextWrite)
            {

                ThrowOnNextWrite = false;

                throw new IOException("write failed");

            }

            CapturedWrittenText.Add(System.Text.Encoding.UTF8.GetString(buffer, offset, count));

        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {

            WritesAttempted++;

            if (ThrowOnNextWrite)
            {

                ThrowOnNextWrite = false;

                return new ValueTask(Task.FromException(new IOException("write failed")));

            }

            CapturedWrittenText.Add(System.Text.Encoding.UTF8.GetString(buffer.Span));

            return default;

        }

    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(
            List<(LogLevel Level, Exception? Exception, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Add((logLevel, exception, formatter(state, exception)));
            }
        }
    }

}
