using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task WriteStreamAsync_NonOperationCanceledException_WritesErrorFrame()
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

        intelligence.NextStreamException = new InvalidOperationException("stream boom");

        PingRequest request = new(Prompt: string.Empty, WorkingDirectory: string.Empty);

        await InferenceExecuteWriter.WriteStreamAsync(httpContext, intelligence, request, cts.Token);

        string output = System.Text.Encoding.UTF8.GetString(body.ToArray());

        Assert.Contains("error", output, StringComparison.OrdinalIgnoreCase);

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

    // W3.4 Group A (S10): a non-disconnect exception thrown by the stream AFTER the response
    // has started must not write an error frame into the partially-streamed NDJSON body
    // (HasStarted guard). The response is started by the first successful token write; the
    // stream then throws InvalidOperationException on the second iteration. With the fix, the
    // general catch observes Response.HasStarted and skips the error-frame write.
    [Fact]
    public async Task WriteStreamAsync_LateStreamExceptionAfterStart_DoesNotWriteErrorFrame()
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

        Assert.DoesNotContain("error", output, StringComparison.OrdinalIgnoreCase);

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

}
