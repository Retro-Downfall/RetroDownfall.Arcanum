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

        WriteSignalStream responseBody = new();

        httpContext.Response.Body = responseBody;

        Task streamTask = SseStreamWriter.StreamAsync(
            httpContext,
            source,
            async (_, _) => await Task.CompletedTask.ConfigureAwait(false),
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        try
        {

            await source.FirstMoveStarted.WaitAsync(TimeSpan.FromSeconds(30));

            await responseBody.FirstWrite.WaitAsync(TimeSpan.FromSeconds(30));

        }
        finally
        {

            source.ReleaseMoves();

        }

        await streamTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(source.ConcurrentMoveDetected, "MoveNextAsync was invoked while a prior move was still pending.");

        Assert.True(source.MoveCount >= 2);

        Assert.True(responseBody.Length > 0, "Expected at least one keep-alive write.");

    }

    // A client that disconnects while the source is idle is detected by the keep-alive write, at
    // which point the enumerator's MoveNextAsync is still in flight. Disposing a compiler-generated
    // async iterator in that state throws NotSupportedException out of the SSE endpoint (the
    // handler only catches OperationCanceledException), turning an ordinary disconnect into an
    // unhandled-exception log on a response that has already started. StreamAsync must unwind the
    // producer and observe the outstanding move before disposing it.
    [Fact]
    public async Task StreamAsync_keep_alive_disconnect_while_a_move_is_pending_does_not_throw()
    {

        ThrowingStream body = new();

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = body;

        body.ThrowOnNextWrite = true;

        await SseStreamWriter.StreamAsync(
            httpContext,
            IdleSourceAsync(),
            static (_, _) => Task.CompletedTask,
            heartbeatInterval: TimeSpan.FromMilliseconds(20),
            CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, body.WritesAttempted);

    }

    // Same hazard, isolated on the shared helper: an async-iterator state machine suspended inside
    // MoveNextAsync must be cancelled and observed before DisposeAsync is allowed to run.
    [Fact]
    public async Task QuiesceAndDisposeAsync_disposes_an_enumerator_with_an_in_flight_move()
    {

        using CancellationTokenSource unwind = new();

        IAsyncEnumerator<string> enumerator = IdleSourceAsync().GetAsyncEnumerator(unwind.Token);

        Task<bool> pendingMove = enumerator.MoveNextAsync().AsTask();

        Assert.False(pendingMove.IsCompleted);

        await SseStreamWriter
            .QuiesceAndDisposeAsync(enumerator, pendingMove, unwind)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(pendingMove.IsCompleted);

    }

    // Every iteration arms a heartbeat timer; a frame that wins the race must release it instead of
    // leaving one TimerQueueTimer (and one registration on the request-linked CTS) alive for the
    // whole heartbeat interval — 30s by default, which at a busy log frame rate is thousands.
    [Fact]
    public async Task StreamAsync_releases_the_heartbeat_timer_of_every_delivered_frame()
    {

        DefaultHttpContext httpContext = new();

        httpContext.Response.Body = new MemoryStream();

        const int frameCount = 200;

        long timersBefore = Timer.ActiveCount;

        await SseStreamWriter.StreamAsync(
            httpContext,
            ManyFramesAsync(frameCount),
            static (_, _) => Task.CompletedTask,
            heartbeatInterval: TimeSpan.FromMinutes(10),
            CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        long retained = Timer.ActiveCount - timersBefore;

        Assert.True(
            retained < frameCount / 4,
            $"Expected abandoned heartbeat timers to be released; {retained.ToString(System.Globalization.CultureInfo.InvariantCulture)} remained active.");

    }

    private static async IAsyncEnumerable<string> IdleSourceAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

        yield return "never-reached";

    }

    private static async IAsyncEnumerable<string> ManyFramesAsync(int count)
    {

        await Task.CompletedTask.ConfigureAwait(false);

        for (int index = 0; index < count; index++)
        {

            yield return $"frame-{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        }

    }

    private sealed class ConcurrentMoveGuardEnumerable : IAsyncEnumerable<string>
    {

        private readonly TaskCompletionSource _firstMoveStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _releaseMoves =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ConcurrentMoveDetected { get; private set; }

        public Task FirstMoveStarted => _firstMoveStarted.Task;

        public int MoveCount { get; private set; }

        public void ReleaseMoves() => _releaseMoves.TrySetResult();

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

                    owner._firstMoveStarted.TrySetResult();

                    // Keep the move pending until the test observes a heartbeat. A buggy writer
                    // would start another MoveNextAsync against this same enumerator meanwhile.
                    await owner._releaseMoves.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

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

    private sealed class WriteSignalStream : MemoryStream
    {

        private readonly TaskCompletionSource _firstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstWrite => _firstWrite.Task;

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            await base.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);

            _firstWrite.TrySetResult();

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
