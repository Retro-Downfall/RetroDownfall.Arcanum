using System.Text;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Reads protocol lines without allowing a peer-controlled line to grow a <see cref="string"/>
/// without bound. Oversize lines are drained and reported so the caller can continue at the next
/// frame boundary.
/// </summary>
internal sealed class BoundedTextLineReader(TextReader reader, int maxLineChars = 1024 * 1024)
{
    internal const int DefaultMaxLineChars = 1024 * 1024;

    private readonly char[] _buffer = new char[4096];

    private int _bufferOffset;

    private int _bufferCount;

    public async ValueTask<BoundedTextLineReadResult> ReadLineAsync(
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineChars, 1);

        StringBuilder line = new(capacity: Math.Min(maxLineChars, 256));

        bool sawCharacter = false;

        bool tooLong = false;

        while (true)
        {
            if (_bufferOffset >= _bufferCount)
            {
                _bufferCount = await reader
                    .ReadAsync(_buffer, cancellationToken)
                    .ConfigureAwait(false);

                _bufferOffset = 0;

                if (_bufferCount == 0)
                {
                    if (!sawCharacter)
                    {
                        return BoundedTextLineReadResult.EndOfStream;
                    }

                    return tooLong
                        ? BoundedTextLineReadResult.FromOversize(
                            TrimTrailingCarriageReturn(line))
                        : BoundedTextLineReadResult.FromLine(TrimTrailingCarriageReturn(line));
                }
            }

            char value = _buffer[_bufferOffset++];

            if (value == '\n')
            {
                return tooLong
                    ? BoundedTextLineReadResult.FromOversize(
                        TrimTrailingCarriageReturn(line))
                    : BoundedTextLineReadResult.FromLine(TrimTrailingCarriageReturn(line));
            }

            sawCharacter = true;

            if (tooLong)
            {
                continue;
            }

            if (line.Length >= maxLineChars)
            {
                tooLong = true;

                continue;
            }

            line.Append(value);
        }
    }

    private static string TrimTrailingCarriageReturn(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        return line.ToString();
    }
}

/// <summary>
/// Decodes a response or pipe stream as it arrives and hands a read back the moment the stream has
/// delivered any characters, the Forge's counterpart of the CLI's reader of the same name.
/// <see cref="StreamReader.ReadAsync(Memory{char}, CancellationToken)"/> does not: it goes back to the
/// stream for more whenever the bytes of the read it just made filled its whole buffer, so a frame the
/// host sent before waiting on the client (an <c>ask_human</c> prompt, a Ward, the final result) was
/// held back whenever its bytes ended on that boundary. This reader makes one stream read per refill,
/// and the line framing in <see cref="BoundedTextLineReader"/> alone decides when a frame is complete.
/// </summary>
/// <remarks>
/// It reads asynchronously only; the synchronous members throw. It drops a leading UTF-8 byte-order
/// mark (also when the mark is split across reads) and, if the stream ends inside a character, drops
/// the incomplete tail, which the framing layer already treats as a disconnect. It never disposes the
/// stream; the caller owns it.
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

                skipped = _chars[0] == '﻿'
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

internal readonly record struct BoundedTextLineReadResult(
    bool HasLine,
    bool IsTooLong,
    string Line)
{
    public static BoundedTextLineReadResult EndOfStream { get; } = new(false, false, string.Empty);

    public static BoundedTextLineReadResult FromOversize(string line) => new(true, true, line);

    public static BoundedTextLineReadResult FromLine(string line) => new(true, false, line);
}
