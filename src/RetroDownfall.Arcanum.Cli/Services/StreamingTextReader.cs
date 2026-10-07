using System.Text;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// Decodes a network stream as it arrives and hands a read back the moment the stream has delivered
/// any characters. <see cref="StreamReader.ReadAsync(Memory{char}, CancellationToken)"/> does not: it
/// goes back to the stream for more whenever the bytes of the read it just made filled its whole
/// buffer, because a full buffer is how it guesses that more is waiting. A server that sends one event
/// and then waits for the client therefore had that event held back whenever its bytes happened to be
/// an exact multiple of the buffer, which for an <c>ask_human</c> prompt meant waiting for an answer
/// to a question that had not been shown. This reader makes one stream read per refill, so it is
/// never ahead of what the server has actually sent, and the line framing in
/// <see cref="BoundedLineReader"/> is the only thing that decides when an event is complete.
/// </summary>
/// <remarks>
/// It reads asynchronously only, because every consumer of a response stream is asynchronous and a
/// blocking read on one is a defect; the synchronous members throw. It decodes UTF-8 or any other
/// encoding whose decoder keeps a partial sequence across calls, drops a leading UTF-8 byte-order mark
/// (also when the mark is split across reads) and, if the stream ends in the middle of a character,
/// drops the incomplete tail: the framing layer already treats a stream that ends mid-line as a
/// disconnect. It does not detect a UTF-16 or UTF-32 mark, as a <see cref="StreamReader"/> can be asked
/// to; the host writes UTF-8 and the SSE format allows nothing else. An encoding that throws on
/// invalid bytes throws from the read that met them. It never disposes the stream; the caller owns it.
/// </remarks>
internal sealed class StreamingTextReader : TextReader
{
    internal const int BufferSize = 4096;

    private readonly Stream _stream;

    private readonly Decoder _decoder;

    private readonly byte[] _bytes = new byte[BufferSize];

    private readonly char[] _chars;

    private int _charPosition;

    private int _charLength;

    private bool _atStart = true;

    public StreamingTextReader(Stream stream, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(stream);

        ArgumentNullException.ThrowIfNull(encoding);

        _stream = stream;

        _decoder = encoding.GetDecoder();

        _chars = new char[encoding.GetMaxCharCount(BufferSize)];
    }

    public override async ValueTask<int> ReadAsync(
        Memory<char> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (_charPosition == _charLength
            && !await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        int count = Math.Min(buffer.Length, _charLength - _charPosition);

        _chars.AsSpan(_charPosition, count).CopyTo(buffer.Span);

        _charPosition += count;

        return count;
    }

    public override Task<int> ReadAsync(char[] buffer, int index, int count) =>
        ReadAsync(buffer.AsMemory(index, count), CancellationToken.None).AsTask();

    public override int Peek() => throw SynchronousReadNotSupported();

    public override int Read() => throw SynchronousReadNotSupported();

    public override int Read(char[] buffer, int index, int count) => throw SynchronousReadNotSupported();

    public override int Read(Span<char> buffer) => throw SynchronousReadNotSupported();

    private static NotSupportedException SynchronousReadNotSupported() =>
        new("A response stream is read asynchronously only.");

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        _charPosition = 0;

        _charLength = 0;

        while (true)
        {
            int byteCount = await _stream
                .ReadAsync(_bytes.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (byteCount == 0)
            {
                return false;
            }

            int decoded = _decoder.GetChars(
                _bytes.AsSpan(0, byteCount),
                _chars,
                flush: false);

            int skipped = 0;

            if (decoded > 0 && _atStart)
            {
                _atStart = false;

                skipped = _chars[0] == '\uFEFF'
                    ? 1
                    : 0;
            }

            if (decoded > skipped)
            {
                _charPosition = skipped;

                _charLength = decoded;

                return true;
            }

            // The bytes ended inside a character (or were only a byte-order mark): the next read of the
            // stream completes it, and the reader still has nothing to hand over.
        }
    }
}
