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

internal readonly record struct BoundedTextLineReadResult(
    bool HasLine,
    bool IsTooLong,
    string Line)
{
    public static BoundedTextLineReadResult EndOfStream { get; } = new(false, false, string.Empty);

    public static BoundedTextLineReadResult FromOversize(string line) => new(true, true, line);

    public static BoundedTextLineReadResult FromLine(string line) => new(true, false, line);
}
