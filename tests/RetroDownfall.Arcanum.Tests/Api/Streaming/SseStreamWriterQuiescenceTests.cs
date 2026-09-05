using System.Text;

using Microsoft.AspNetCore.Http;

using RetroDownfall.Arcanum.Api.Streaming;

namespace RetroDownfall.Arcanum.Tests.Api.Streaming;

/// <summary>
/// What a maintenance revocation does to a stream that is in the middle of writing.
/// </summary>
/// <remarks>
/// Every assertion here is about a boundary rather than a count, because "stops without a partial
/// frame" is the acceptance criterion and a partial frame is invisible to any test that only counts
/// frames. The barrier is a <see cref="TaskCompletionSource"/> so the revocation lands provably
/// inside a frame write rather than probably inside one; a sleep would make this suite pass on a
/// slow machine for the wrong reason.
/// </remarks>
public sealed class SseStreamWriterQuiescenceTests
{

    private static readonly TimeSpan BoundedWait = TimeSpan.FromSeconds(30);

    private const string Done = "data: [DONE]\n\n";

    /// <summary>
    /// A frame already being written finishes, whole, after revocation lands mid-write.
    /// </summary>
    /// <remarks>
    /// The frame writer parks between its first and second write, the test revokes, and the writer
    /// then completes the frame on the token it was handed. If revocation had reached that token the
    /// second write would throw and the response would end on a <c>data:</c> line with no terminating
    /// blank line — bytes no client can parse and no later frame can repair.
    /// </remarks>
    [Fact]
    public async Task A_frame_already_being_written_is_finished_whole()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        using CancellationTokenSource revocation = new();

        GrimoireStreamQuiescence quiescence = new(revocation.Token);

        TaskCompletionSource insideFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        ControlledSource source = new();

        source.Yield("first");

        Task stream = SseStreamWriter.StreamAsync(
            context,
            source,
            async (string item, CancellationToken frameToken) =>
            {

                await body.WriteAsync(Encoding.UTF8.GetBytes($"data: {item}"), frameToken)
                    .ConfigureAwait(false);

                insideFrame.TrySetResult();

                await revoked.Task.ConfigureAwait(false);

                // The terminating blank line is written after revocation, on the frame token. A
                // token that answered to revocation would throw here.
                await body.WriteAsync(Encoding.UTF8.GetBytes("\n\n"), frameToken).ConfigureAwait(false);

            },
            TimeSpan.Zero,
            quiescence,
            CancellationToken.None);

        await insideFrame.Task.WaitAsync(BoundedWait);

        await revocation.CancelAsync();

        revoked.SetResult();

        await stream.WaitAsync(BoundedWait);

        Assert.Equal($"data: first\n\n{Done}", body.Text);

    }

    /// <summary>
    /// No frame and no keep-alive is written once revocation has been observed.
    /// </summary>
    [Fact]
    public async Task No_further_frame_begins_after_revocation()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        using CancellationTokenSource revocation = new();

        GrimoireStreamQuiescence quiescence = new(revocation.Token);

        ControlledSource source = new();

        source.Yield("first");

        source.Yield("second");

        source.Yield("third");

        int framesWritten = 0;

        await SseStreamWriter.StreamAsync(
            context,
            source,
            async (string item, CancellationToken frameToken) =>
            {

                framesWritten++;

                await body.WriteAsync(Encoding.UTF8.GetBytes($"data: {item}\n\n"), frameToken)
                    .ConfigureAwait(false);

                await revocation.CancelAsync().ConfigureAwait(false);

            },
            TimeSpan.Zero,
            quiescence,
            CancellationToken.None).WaitAsync(BoundedWait);

        Assert.Equal(1, framesWritten);

        Assert.Equal($"data: first\n\n{Done}", body.Text);

    }

    /// <summary>
    /// A request already quiescing when the live loop is entered starts no frame at all.
    /// </summary>
    /// <remarks>
    /// This is the path the two replay routes fall through on: they stop between their own frames and
    /// enter the writer anyway, so the one terminal frame is written in one place rather than in each
    /// route's own early return.
    /// </remarks>
    [Fact]
    public async Task A_stream_that_is_already_quiescing_writes_only_the_terminal_frame()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        using CancellationTokenSource revocation = new();

        await revocation.CancelAsync();

        ControlledSource source = new();

        source.Yield("never-written");

        int framesWritten = 0;

        await SseStreamWriter.StreamAsync(
            context,
            source,
            (string _, CancellationToken _) =>
            {

                framesWritten++;

                return Task.CompletedTask;

            },
            TimeSpan.Zero,
            new GrimoireStreamQuiescence(revocation.Token),
            CancellationToken.None).WaitAsync(BoundedWait);

        Assert.Equal(0, framesWritten);

        Assert.Equal(Done, body.Text);

        Assert.False(source.MoveNextWasCalled);

    }

    /// <summary>
    /// Revocation cancels the producer's enumeration token and never the frame token.
    /// </summary>
    [Fact]
    public async Task The_producer_is_cancelled_and_the_frame_token_is_not()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        using CancellationTokenSource revocation = new();

        ControlledSource source = new();

        source.Yield("first");

        CancellationToken observedFrameToken = default;

        await SseStreamWriter.StreamAsync(
            context,
            source,
            async (string _, CancellationToken frameToken) =>
            {

                observedFrameToken = frameToken;

                await revocation.CancelAsync().ConfigureAwait(false);

            },
            TimeSpan.Zero,
            new GrimoireStreamQuiescence(revocation.Token),
            CancellationToken.None).WaitAsync(BoundedWait);

        Assert.True(source.EnumerationToken.IsCancellationRequested);

        Assert.False(observedFrameToken.IsCancellationRequested);

    }

    /// <summary>
    /// The enumerator is disposed, and only after the outstanding move has been observed.
    /// </summary>
    /// <remarks>
    /// The invariant DESIGN §10.7.1 already records for a client disconnect holds identically for a
    /// maintenance quiesce: a compiler-generated async iterator throws from <c>DisposeAsync</c> while
    /// a <c>MoveNextAsync</c> is still in flight.
    /// </remarks>
    [Fact]
    public async Task The_producer_is_observed_before_the_enumerator_is_disposed()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        using CancellationTokenSource revocation = new();

        ControlledSource source = new();

        source.Yield("first");

        source.ParkAfterYields = true;

        Task stream = SseStreamWriter.StreamAsync(
            context,
            source,
            (string _, CancellationToken _) => Task.CompletedTask,
            TimeSpan.Zero,
            new GrimoireStreamQuiescence(revocation.Token),
            CancellationToken.None);

        await source.Parked.Task.WaitAsync(BoundedWait);

        await revocation.CancelAsync();

        await stream.WaitAsync(BoundedWait);

        Assert.True(source.Disposed);

        Assert.False(source.DisposedWhileMoving);

        Assert.Equal(Done, body.Text);

    }

    /// <summary>
    /// A producer that ends on its own still writes no terminal frame from the writer.
    /// </summary>
    /// <remarks>
    /// The routes' own cancellation arms already write <c>[DONE]</c> when the host stops them, and the
    /// live SSE routes deliberately end silently when a producer simply completes. Writing a terminal
    /// frame on every ordinary end would change a wire contract this child is not allowed to touch.
    /// </remarks>
    [Fact]
    public async Task An_ordinary_end_of_stream_writes_no_terminal_frame()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        ControlledSource source = new();

        source.Yield("only");

        await SseStreamWriter.StreamAsync(
            context,
            source,
            async (string item, CancellationToken frameToken) =>
                await body.WriteAsync(Encoding.UTF8.GetBytes($"data: {item}\n\n"), frameToken)
                    .ConfigureAwait(false),
            TimeSpan.Zero,
            new GrimoireStreamQuiescence(CancellationToken.None),
            CancellationToken.None).WaitAsync(BoundedWait);

        Assert.Equal("data: only\n\n", body.Text);

    }

    /// <summary>
    /// The same boundary holds on the heartbeat branch, where a delay races the pending move.
    /// </summary>
    [Fact]
    public async Task The_heartbeat_branch_stops_at_the_same_boundary()
    {

        RecordingBody body = new();

        DefaultHttpContext context = new();

        context.Response.Body = body;

        using CancellationTokenSource revocation = new();

        ControlledSource source = new();

        source.Yield("first");

        source.ParkAfterYields = true;

        Task stream = SseStreamWriter.StreamAsync(
            context,
            source,
            async (string item, CancellationToken frameToken) =>
                await body.WriteAsync(Encoding.UTF8.GetBytes($"data: {item}\n\n"), frameToken)
                    .ConfigureAwait(false),
            TimeSpan.FromMilliseconds(20),
            new GrimoireStreamQuiescence(revocation.Token),
            CancellationToken.None);

        await source.Parked.Task.WaitAsync(BoundedWait);

        await revocation.CancelAsync();

        await stream.WaitAsync(BoundedWait);

        Assert.EndsWith(Done, body.Text, StringComparison.Ordinal);

        Assert.StartsWith("data: first\n\n", body.Text, StringComparison.Ordinal);

        Assert.True(source.Disposed);

        // Keep-alive comments may or may not have raced in before the revocation; what must never
        // appear is a second data frame or bytes after the terminal one.
        Assert.EndsWith(Done, body.Text, StringComparison.Ordinal);

        Assert.DoesNotContain("data: second", body.Text, StringComparison.Ordinal);

    }

    /// <summary>
    /// A source whose moves and disposal are observable, and which can park on demand.
    /// </summary>
    private sealed class ControlledSource : IAsyncEnumerable<string>
    {

        private readonly Queue<string> _items = new();

        internal TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool ParkAfterYields { get; set; }

        internal bool Disposed { get; private set; }

        internal bool DisposedWhileMoving { get; private set; }

        internal bool MoveNextWasCalled { get; private set; }

        internal CancellationToken EnumerationToken { get; private set; }

        internal void Yield(string item) => _items.Enqueue(item);

        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {

            EnumerationToken = cancellationToken;

            return new Enumerator(this, cancellationToken);

        }

        private sealed class Enumerator(ControlledSource owner, CancellationToken cancellationToken)
            : IAsyncEnumerator<string>
        {

            private bool _moving;

            public string Current { get; private set; } = string.Empty;

            public async ValueTask<bool> MoveNextAsync()
            {

                owner.MoveNextWasCalled = true;

                _moving = true;

                try
                {

                    if (owner._items.Count > 0)
                    {

                        Current = owner._items.Dequeue();

                        return true;

                    }

                    if (!owner.ParkAfterYields)
                    {

                        return false;

                    }

                    owner.Parked.TrySetResult();

                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);

                    return false;

                }
                finally
                {

                    _moving = false;

                }

            }

            public ValueTask DisposeAsync()
            {

                if (_moving)
                {

                    owner.DisposedWhileMoving = true;

                }

                owner.Disposed = true;

                return ValueTask.CompletedTask;

            }

        }

    }

    /// <summary>A response body that keeps every byte written to it, in order.</summary>
    private sealed class RecordingBody : Stream
    {

        private readonly MemoryStream _written = new();

        private readonly Lock _sync = new();

        internal string Text
        {

            get
            {

                lock (_sync)
                {

                    return Encoding.UTF8.GetString(_written.ToArray());

                }

            }

        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _written.Length;

        public override long Position
        {
            get => _written.Position;
            set => throw new NotSupportedException();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {

            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {

                _written.Write(buffer.Span);

            }

            return ValueTask.CompletedTask;

        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(byte[] buffer, int offset, int count)
        {

            lock (_sync)
            {

                _written.Write(buffer, offset, count);

            }

        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {

            cancellationToken.ThrowIfCancellationRequested();

            return Task.CompletedTask;

        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

    }

}
