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

        public bool ThrowOnNextWrite { get; set; }

        public int WritesAttempted { get; private set; }

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

                throw new IOException("write failed");

            }

        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {

            WritesAttempted++;

            if (ThrowOnNextWrite)
            {

                return new ValueTask(Task.FromException(new IOException("write failed")));

            }

            return default;

        }

    }

}
