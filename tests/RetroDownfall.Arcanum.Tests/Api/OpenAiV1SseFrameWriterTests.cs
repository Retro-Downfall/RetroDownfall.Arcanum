using System.Buffers;
using System.Text;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using Microsoft.AspNetCore.Http;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Frame-level behaviour of the <c>/v1</c> SSE writer, independent of any host: one frame must reach
/// the response body as one write, so a failure part-way through cannot leave a committed
/// <c>data: </c> prefix with no payload behind it and corrupt whatever is written next.
/// </summary>
public sealed class OpenAiV1SseFrameWriterTests
{

    [Fact]
    public async Task WriteSseJsonAsync_CommitsTheWholeFrameInOneWrite()
    {

        RecordingStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = body;

        ArrayBufferWriter<byte> buffer = new(512);

        OpenAiChatChunk chunk = new(
            Id: "chatcmpl-frame",
            ObjectKind: "chat.completion.chunk",
            Created: 0,
            Model: "test-model",
            Choices: []);

        await OpenAiV1Endpoints.WriteSseJsonAsync(
            httpContext,
            buffer,
            chunk,
            ArcanumJsonContext.Default.OpenAiChatChunk,
            CancellationToken.None);

        string frame = Assert.Single(body.Writes);

        Assert.StartsWith("data: ", frame, StringComparison.Ordinal);

        Assert.EndsWith("\n\n", frame, StringComparison.Ordinal);

        Assert.Contains("chatcmpl-frame", frame, StringComparison.Ordinal);

    }

    [Fact]
    public async Task WriteSseJsonAsync_FailurePartWayThroughAFrame_NeverCommitsABarePrefix()
    {

        RecordingStream body = new() { ThrowOnWriteNumber = 2 };

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = body;

        ArrayBufferWriter<byte> buffer = new(512);

        OpenAiChatChunk chunk = new(
            Id: "chatcmpl-frame",
            ObjectKind: "chat.completion.chunk",
            Created: 0,
            Model: "test-model",
            Choices: []);

        try
        {

            await OpenAiV1Endpoints.WriteSseJsonAsync(
                httpContext,
                buffer,
                chunk,
                ArcanumJsonContext.Default.OpenAiChatChunk,
                CancellationToken.None);

        }
        catch (IOException)
        {
        }

        // Whatever reached the socket is a whole frame. A committed bare `data: ` would make the next
        // frame land behind it as a malformed `data: data: {...}`.
        Assert.DoesNotContain(
            body.Writes,
            static written => !written.EndsWith("\n\n", StringComparison.Ordinal));

    }

    private sealed class RecordingStream : Stream
    {

        public List<string> Writes { get; } = [];

        /// <summary>1-based index of the write that throws; zero (the default) never throws.</summary>
        public int ThrowOnWriteNumber { get; init; }

        private int _writeCount;

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
            Record(new ReadOnlySpan<byte>(buffer, offset, count));

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {

            try
            {

                Record(buffer.Span);

            }
            catch (IOException exception)
            {

                return new ValueTask(Task.FromException(exception));

            }

            return default;

        }

        private void Record(ReadOnlySpan<byte> buffer)
        {

            _writeCount++;

            if (_writeCount == ThrowOnWriteNumber)
            {

                throw new IOException("write failed");

            }

            Writes.Add(Encoding.UTF8.GetString(buffer));

        }

    }

}
