using Microsoft.AspNetCore.Http;
using System.Net.Http;
using System.Text;
using RetroDownfall.Arcanum.Api.Streaming;

namespace RetroDownfall.Arcanum.Tests.Api.Streaming;

public sealed class SseStreamWriterTests
{

    // W3.4 Group A (S10): a broken-pipe IOException from the per-frame write callback must
    // not propagate out of SseStreamWriter.StreamAsync. The caller's catch(OperationCanceledException)
    // only handles clean cancellation; an unhandled IOException would surface as an
    // unhandled-exception log. With the fix, StreamAsync treats the IOException as a client
    // disconnect and returns silently (the caller's `using`/`finally` then disposes the linked
    // CTS, cancelling the producer). The same catch clause also covers the heartbeat
    // keep-alive write site inside StreamAsync.
    [Fact]
    public async Task StreamAsync_client_disconnect_via_IOException_returns_silently()
    {

        ThrowingStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = body;

        body.ThrowOnNextWrite = true;

        CancellationTokenSource cts = new();

        int framesWritten = 0;

        await SseStreamWriter.StreamAsync(
            httpContext,
            SourceAsync(),
            async (item, ct) =>
            {

                framesWritten++;

                byte[] bytes = Encoding.UTF8.GetBytes(item);

                await httpContext.Response.Body.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);

            },
            TimeSpan.Zero,
            cts.Token);

        Assert.Equal(1, framesWritten);

        Assert.Equal(1, body.WritesAttempted);

    }

    // W3.4 Group A (S10): HttpIOException (the System.Net.Http wrapper for client-reset on
    // the response body) must be treated identically to IOException — silent return, no
    // unhandled-exception log.
    [Fact]
    public async Task StreamAsync_client_disconnect_via_HttpIOException_returns_silently()
    {

        HttpThrowingStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = body;

        int framesWritten = 0;

        await SseStreamWriter.StreamAsync(
            httpContext,
            SourceAsync(),
            async (item, ct) =>
            {

                framesWritten++;

                byte[] bytes = Encoding.UTF8.GetBytes(item);

                await httpContext.Response.Body.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);

            },
            TimeSpan.Zero,
            CancellationToken.None);

        Assert.Equal(1, framesWritten);

        Assert.Equal(1, body.WritesAttempted);

    }

    [Fact]
    public async Task StreamAsync_with_heartbeat_never_calls_MoveNextAsync_concurrently()
    {

        ConcurrentMoveGuardEnumerable source = new();

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = new MemoryStream();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));

        await SseStreamWriter.StreamAsync(
            httpContext,
            source,
            async (_, _) => await Task.CompletedTask.ConfigureAwait(false),
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            cts.Token);

        Assert.False(source.ConcurrentMoveDetected, "MoveNextAsync was invoked while a prior move was still pending.");

        Assert.True(source.MoveCount >= 2);

        Assert.True(httpContext.Response.Body.Length > 0, "Expected at least one keep-alive write.");

    }

    private sealed class ConcurrentMoveGuardEnumerable : IAsyncEnumerable<string>
    {

        public bool ConcurrentMoveDetected { get; private set; }

        public int MoveCount { get; private set; }

        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new Enumerator(this, cancellationToken);

        private sealed class Enumerator(ConcurrentMoveGuardEnumerable owner, CancellationToken cancellationToken) : IAsyncEnumerator<string>
        {

            private int _inFlight;

            private int _yielded;

            public string Current { get; private set; } = string.Empty;

            public ValueTask DisposeAsync() => default;

            public async ValueTask<bool> MoveNextAsync()
            {

                if (Interlocked.Exchange(ref _inFlight, 1) == 1)
                {

                    owner.ConcurrentMoveDetected = true;

                }

                try
                {

                    owner.MoveCount++;

                    // Hold the move long enough that a heartbeat interval can elapse while
                    // MoveNextAsync is still pending — the bug would start a second move here.
                    await Task.Delay(60, cancellationToken).ConfigureAwait(false);

                    if (_yielded >= 2)
                    {

                        return false;

                    }

                    _yielded++;

                    Current = $"item-{_yielded}";

                    return true;

                }
                finally
                {

                    Interlocked.Exchange(ref _inFlight, 0);

                }

            }

        }

    }

    private static async IAsyncEnumerable<string> SourceAsync()
    {

        await Task.CompletedTask.ConfigureAwait(false);

        yield return "frame-1";

        yield return "frame-2";

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

                ThrowOnNextWrite = false;

                throw new IOException("broken pipe");

            }

        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {

            WritesAttempted++;

            if (ThrowOnNextWrite)
            {

                ThrowOnNextWrite = false;

                return new ValueTask(Task.FromException(new IOException("broken pipe")));

            }

            return default;

        }

    }

    private sealed class HttpThrowingStream : Stream
    {

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

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new HttpIOException(HttpRequestError.ConnectionError, "reset");

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {

            WritesAttempted++;

            return new ValueTask(Task.FromException(new HttpIOException(HttpRequestError.ConnectionError, "reset")));

        }

    }

}
