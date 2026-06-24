using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Intelligence;
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
