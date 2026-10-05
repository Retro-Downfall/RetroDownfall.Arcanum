using System.Text;

using System.Threading.Channels;

using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Pins the SSE frame parser against the delivery shapes a real socket produces (one byte at a time,
/// a code point or a CRLF pair split across reads), its idle-window timer cost, and its bounds on
/// what a single line or a joined event may buffer.
/// </summary>
public sealed class WatchSseParserTests
{
    /// <summary>
    /// Hang guard for every await in this file, not a behavioural assertion: the parser is driven by
    /// scripted streams and delays, so these timeouts only turn a deadlock into a failure.
    /// </summary>
    private static readonly TimeSpan AsyncTestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The transport hands the reader whatever the socket had, and a socket can have one byte. The
    /// four-byte emoji is split in three places and every CRLF pair arrives as two reads, so a decoder
    /// or line splitter that assumes a whole code point or a whole terminator per read fails here.
    /// </summary>
    [Fact]
    public async Task Frames_survive_one_byte_reads_including_split_utf8_and_crlf()
    {
        const string Emoji = "\U0001F600";

        string wire = "event: log\r\n"
            + "data: {\"message\":\"" + Emoji + "\"}\r\n"
            + "\r\n"
            + ": keep-alive\r\n"
            + "\r\n"
            + "data: [DONE]\r\n"
            + "\r\n";

        using OneByteReadStream stream = new(Encoding.UTF8.GetBytes(wire));

        using StreamReader reader = CreateProductionReader(stream);

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(reader, CancellationToken.None));

        Assert.Collection(
            frames,
            data =>
            {
                Assert.Equal(WatchSseFrameType.Data, data.Type);

                Assert.Equal(
                    Emoji,
                    data.Data!.Value.GetProperty("message").GetString());
            },
            heartbeat => Assert.Equal(WatchSseFrameType.Heartbeat, heartbeat.Type),
            done => Assert.Equal(WatchSseFrameType.Done, done.Type));

        Assert.Equal(1, stream.LargestRead);
    }

    /// <summary>
    /// A line (here a lone CR terminated one, then a CRLF one) is complete the moment its terminator
    /// arrives; a trailing LF that shows up in a later read must be swallowed, not read as an empty
    /// line that would end the event early.
    /// </summary>
    [Fact]
    public async Task A_line_feed_arriving_in_a_later_read_does_not_end_the_event()
    {
        await using ScriptedStream stream = new();

        using StreamReader reader = CreateProductionReader(stream);

        await using IAsyncEnumerator<WatchSseFrame> frames = WatchSseParser
            .ParseAsync(reader, CancellationToken.None)
            .GetAsyncEnumerator();

        stream.Push("data: {\"a\":\r");

        stream.Push("\ndata: 1}\r");

        stream.Push("\n\r");

        stream.Push("\ndata: [DONE]\r\n\r\n");

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Data, frames.Current.Type);

        Assert.Equal(1, frames.Current.Data!.Value.GetProperty("a").GetInt32());

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Done, frames.Current.Type);
    }

    /// <summary>
    /// The parser reads the stream in fixed chunks, so a CRLF can straddle two of them as well as two
    /// socket reads. The CR is the last character of the first chunk here; the LF that opens the next
    /// must not become an empty line that ends the event after its first data line.
    /// </summary>
    [Fact]
    public async Task A_crlf_pair_split_by_the_internal_chunk_boundary_is_one_terminator()
    {
        const int ChunkLength = 4096;

        const string Prefix = "data: {\"a\":\"";

        const string Suffix = "\",";

        string padding = new('p', ChunkLength - 1 - Prefix.Length - Suffix.Length);

        string firstLine = Prefix + padding + Suffix;

        Assert.Equal(ChunkLength - 1, firstLine.Length);

        using StringReader reader = new(
            firstLine + "\r\n"
            + "data: \"b\":1}\r\n"
            + "\r\n"
            + "data: [DONE]\r\n"
            + "\r\n");

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(reader, CancellationToken.None));

        Assert.Collection(
            frames,
            data =>
            {
                Assert.Equal(WatchSseFrameType.Data, data.Type);

                Assert.Equal(
                    padding.Length,
                    data.Data!.Value.GetProperty("a").GetString()!.Length);

                Assert.Equal(1, data.Data!.Value.GetProperty("b").GetInt32());
            },
            done => Assert.Equal(WatchSseFrameType.Done, done.Type));
    }

    /// <summary>
    /// The limit is inclusive: a line of exactly <c>maxEventLength</c> characters is delivered, and one
    /// character more is rejected.
    /// </summary>
    [Fact]
    public async Task A_line_exactly_at_the_limit_is_kept_and_one_character_over_is_rejected()
    {
        const int Limit = 64;

        const string Prefix = "data: {\"m\":\"";

        const string Suffix = "\"}";

        string atLimit = Prefix + new string('a', Limit - Prefix.Length - Suffix.Length) + Suffix;

        string overLimit = Prefix + new string('a', Limit - Prefix.Length - Suffix.Length + 1) + Suffix;

        Assert.Equal(Limit, atLimit.Length);

        Assert.Equal(Limit + 1, overLimit.Length);

        using StringReader reader = new(
            atLimit + "\n\n"
            + overLimit + "\n\n"
            + "data: [DONE]\n\n");

        List<WatchSseFrame> frames = await CollectAsync(
            ParseWithLimit(reader, Limit));

        Assert.Collection(
            frames,
            kept => Assert.Equal(WatchSseFrameType.Data, kept.Type),
            rejected => Assert.Equal(WatchSseFrameType.Error, rejected.Type),
            done => Assert.Equal(WatchSseFrameType.Done, done.Type));
    }

    /// <summary>
    /// An event over the limit used to be buffered whole and parsed. It is now rejected the way a
    /// malformed event is (a recoverable <c>Api.InvalidResponse</c> frame), and the next event on
    /// the same stream is delivered normally.
    /// </summary>
    [Fact]
    public async Task Oversized_event_is_rejected_as_malformed()
    {
        string oversized = new('x', 400);

        using StringReader reader = new(
            $"data: {{\"message\":\"{oversized}\"}}\n\n"
            + "data: {\"ok\":1}\n\n"
            + "data: [DONE]\n\n");

        List<WatchSseFrame> frames = await CollectAsync(
            ParseWithLimit(reader, maxEventLength: 128));

        Assert.Collection(
            frames,
            rejected =>
            {
                Assert.Equal(WatchSseFrameType.Error, rejected.Type);

                Assert.Equal("Api.InvalidResponse", rejected.Error!.Value.Code);

                Assert.True(rejected.Recoverable);

                Assert.Null(rejected.Data);
            },
            accepted =>
            {
                Assert.Equal(WatchSseFrameType.Data, accepted.Type);

                Assert.Equal(1, accepted.Data!.Value.GetProperty("ok").GetInt32());
            },
            done => Assert.Equal(WatchSseFrameType.Done, done.Type));
    }

    /// <summary>
    /// Each <c>data:</c> line is under the limit; their joined payload is not. The total is what a
    /// consumer would have to hold, so it is the thing bounded.
    /// </summary>
    [Fact]
    public async Task Event_whose_data_lines_add_up_past_the_limit_is_rejected()
    {
        string half = new('y', 30);

        using StringReader reader = new(
            $"data: {{\"a\":\"{half}\",\n"
            + $"data: \"b\":\"{half}\"}}\n"
            + "\n"
            + "data: [DONE]\n\n");

        List<WatchSseFrame> frames = await CollectAsync(
            ParseWithLimit(reader, maxEventLength: 64));

        Assert.Collection(
            frames,
            rejected =>
            {
                Assert.Equal(WatchSseFrameType.Error, rejected.Type);

                Assert.True(rejected.Recoverable);
            },
            done => Assert.Equal(WatchSseFrameType.Done, done.Type));
    }

    /// <summary>
    /// A stream that ends inside an oversized event still says why that event was lost before it
    /// reports the disconnect.
    /// </summary>
    [Fact]
    public async Task Oversized_event_cut_off_by_the_end_of_the_stream_is_reported_before_the_disconnect()
    {
        using StringReader reader = new(
            "data: {\"message\":\"" + new string('z', 1000) + "\"}");

        List<WatchSseFrame> frames = await CollectAsync(
            ParseWithLimit(reader, maxEventLength: 64));

        Assert.Collection(
            frames,
            rejected => Assert.Equal(WatchSseFrameType.Error, rejected.Type),
            eof => Assert.Equal(WatchSseFrameType.UnexpectedEof, eof.Type));
    }

    /// <summary>
    /// The idle timer used to be rebuilt (a linked token source plus a timer) for every line that
    /// arrived. A window now arms one delay, and lines that arrive inside it reuse it.
    /// </summary>
    [Fact]
    public async Task Idle_window_arms_one_delay_instead_of_one_per_line()
    {
        StringBuilder wire = new();

        for (int index = 0; index < 50; index++)
        {
            wire.Append(": ping ").Append(index).Append('\n');
        }

        wire.Append("data: [DONE]\n\n");

        using StringReader reader = new(wire.ToString());

        int delayCalls = 0;

        Task CountingDelay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref delayCalls);

            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        List<WatchSseFrame> frames = await CollectAsync(
            WatchSseParser.ParseAsync(
                reader,
                TimeSpan.FromMinutes(1),
                CountingDelay,
                CancellationToken.None));

        Assert.Equal(WatchSseFrameType.Done, frames[^1].Type);

        Assert.Equal(1, delayCalls);
    }

    /// <summary>
    /// A window that expires with lines having arrived inside it was not idle for its whole length,
    /// so it reports nothing and a fresh window starts; the diagnostic comes from a window that saw
    /// no activity at all.
    /// </summary>
    [Fact]
    public async Task Activity_inside_a_window_defers_the_idle_diagnostic_to_the_next_window()
    {
        await using ScriptedStream stream = new();

        using StreamReader reader = CreateProductionReader(stream);

        WindowedDelay delays = new();

        await using IAsyncEnumerator<WatchSseFrame> frames = WatchSseParser
            .ParseAsync(
                reader,
                TimeSpan.FromMinutes(1),
                delays.WaitAsync,
                CancellationToken.None)
            .GetAsyncEnumerator();

        stream.Push(": keep-alive\n");

        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(AsyncTestTimeout));

        Assert.Contains("keep-alive", frames.Current.Diagnostic!, StringComparison.OrdinalIgnoreCase);

        Task<bool> nextFrame = frames.MoveNextAsync().AsTask();

        await delays.Armed(0).WaitAsync(AsyncTestTimeout);

        delays.Expire(0);

        await delays.Armed(1).WaitAsync(AsyncTestTimeout);

        Assert.False(nextFrame.IsCompleted);

        delays.Expire(1);

        Assert.True(await nextFrame.WaitAsync(AsyncTestTimeout));

        Assert.Equal(WatchSseFrameType.Heartbeat, frames.Current.Type);

        Assert.Contains("No stream activity", frames.Current.Diagnostic!, StringComparison.Ordinal);
    }

    private static IAsyncEnumerable<WatchSseFrame> ParseWithLimit(
        TextReader reader,
        int maxEventLength) =>
        WatchSseParser.ParseAsync(
            reader,
            WatchSseParser.IdleDiagnosticInterval,
            static (delay, token) => Task.Delay(delay, token),
            maxEventLength,
            CancellationToken.None);

    /// <summary>
    /// The reader <c>WatchSseAsync</c> builds over the response stream.
    /// </summary>
    private static StreamReader CreateProductionReader(Stream stream) =>
        new(
            stream,
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

    private static async Task<List<WatchSseFrame>> CollectAsync(
        IAsyncEnumerable<WatchSseFrame> source)
    {
        List<WatchSseFrame> frames = [];

        await foreach (WatchSseFrame frame in source)
        {
            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>
    /// Serves the bytes it was given but never more than one per read, on both the sync and the async
    /// path, and remembers the largest read it was asked to satisfy so a test can prove it was in the
    /// loop.
    /// </summary>
    private sealed class OneByteReadStream(byte[] content) : Stream
    {
        private int _position;

        public int LargestRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();

            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= content.Length)
            {
                return 0;
            }

            buffer[0] = content[_position++];

            LargestRead = Math.Max(LargestRead, 1);

            return 1;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A read-only stream the test feeds chunk by chunk; a read that finds nothing waits for the next
    /// chunk, so the parser's idle and read paths run exactly as they do on a quiet socket.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        private byte[] _current = [];

        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();

            set => throw new NotSupportedException();
        }

        public void Push(string text) =>
            _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (_chunks.Reader.TryRead(out byte[]? next))
                {
                    _current = next;

                    _offset = 0;
                }
            }

            int count = Math.Min(buffer.Length, _current.Length - _offset);

            _current.AsMemory(_offset, count).CopyTo(buffer);

            _offset += count;

            return count;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The parser's injected clock: every call is one idle window, announced by <see cref="Armed"/>
    /// and expired by <see cref="Expire"/>, so a test decides exactly when each window ends.
    /// </summary>
    private sealed class WindowedDelay
    {
        private readonly object _gate = new();

        private readonly List<TaskCompletionSource> _expiry = [];

        private readonly List<TaskCompletionSource> _armed = [];

        public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            TaskCompletionSource expiry = NewSource();

            lock (_gate)
            {
                int index = _expiry.Count;

                _expiry.Add(expiry);

                ArmedSource(index).TrySetResult();
            }

            return expiry.Task.WaitAsync(cancellationToken);
        }

        public Task Armed(int index)
        {
            lock (_gate)
            {
                return ArmedSource(index).Task;
            }
        }

        public void Expire(int index)
        {
            lock (_gate)
            {
                _expiry[index].TrySetResult();
            }
        }

        private TaskCompletionSource ArmedSource(int index)
        {
            while (_armed.Count <= index)
            {
                _armed.Add(NewSource());
            }

            return _armed[index];
        }

        private static TaskCompletionSource NewSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
