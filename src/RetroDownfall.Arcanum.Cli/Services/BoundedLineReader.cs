using System.Globalization;

using System.Text;

namespace RetroDownfall.Arcanum.Cli.Services;

/// <summary>
/// One line of a stream. A line over the limit is not kept: <see cref="TooLong"/> says it existed.
/// </summary>
internal readonly record struct BoundedLine(string Text, bool TooLong);

/// <summary>
/// Splits a character stream into lines on LF, CR or CRLF, the three terminators the SSE format
/// allows, without ever holding more than <c>maxLineLength</c> characters of one line. A line is
/// complete the moment its terminator arrives; a LF that follows a CR in a later read is swallowed
/// rather than read as an empty line. Every line-framed stream the CLI reads (the watch SSE streams
/// and the NDJSON ask, research and Chronicle streams) goes through it, so one runaway line cannot
/// grow the client without bound on any of them.
/// </summary>
internal sealed class BoundedLineReader(TextReader reader, int maxLineLength)
{
    /// <summary>
    /// The most characters one line may hold before it is discarded. It is a runaway guard, not a
    /// payload policy: the server's own ceiling on one tool output is 64 MiB, so an ordinary record is
    /// far below it, while a stream that never ends a line cannot grow the client without bound.
    /// Holding one line at the limit costs several hundred MiB transiently (the line builder, its
    /// string, and the copies the consumers make), which is why it is no larger.
    /// </summary>
    internal const int DefaultMaxLineLength = 64 * 1024 * 1024;

    private readonly char[] _buffer = new char[4096];

    private readonly StringBuilder _line = new();

    private int _start;

    private int _end;

    private bool _skipLineFeed;

    private bool _lineTooLong;

    /// <summary>
    /// What a consumer reports when it discards an event or line over the limit. It names the owner
    /// (the CLI's stream reader), the limit, what is still saved (the host's record) and the way to
    /// recover it, as the retained-boundary diagnostics in the Command Reference require. Whether the
    /// stream goes on afterwards is the consumer's own contract, so the text does not promise it.
    /// </summary>
    internal static string DescribeOversizedLine(int maxLineLength) =>
        "The CLI stream reader discarded an event larger than its limit of "
        + maxLineLength.ToString("N0", CultureInfo.InvariantCulture)
        + " characters. The Arcanum host still holds the full record: read it with the matching "
        + "show or export command.";

    public async Task<BoundedLine?> ReadLineAsync(CancellationToken cancellationToken)
    {
        _line.Clear();

        _lineTooLong = false;

        while (true)
        {
            if (_start == _end)
            {
                _start = 0;

                _end = 0;

                _end = await reader
                    .ReadAsync(_buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                if (_end == 0)
                {
                    return _lineTooLong || _line.Length > 0
                        ? CompleteLine()
                        : null;
                }
            }

            if (TryConsumeLine(out BoundedLine line))
            {
                return line;
            }
        }
    }

    private bool TryConsumeLine(out BoundedLine line)
    {
        line = default;

        if (_skipLineFeed)
        {
            _skipLineFeed = false;

            if (_buffer[_start] == '\n')
            {
                _start++;

                return false;
            }
        }

        ReadOnlySpan<char> available = _buffer.AsSpan(_start, _end - _start);

        int terminator = available.IndexOfAny('\r', '\n');

        if (terminator < 0)
        {
            Append(available);

            _start = _end;

            return false;
        }

        ReadOnlySpan<char> segment = available[..terminator];

        bool carriageReturn = available[terminator] == '\r';

        _start += terminator + 1;

        if (carriageReturn)
        {
            if (_start == _end)
            {
                _skipLineFeed = true;
            }
            else if (_buffer[_start] == '\n')
            {
                _start++;
            }
        }

        if (_line.Length == 0
            && !_lineTooLong
            && segment.Length <= maxLineLength)
        {
            line = new BoundedLine(new string(segment), TooLong: false);

            return true;
        }

        Append(segment);

        line = CompleteLine();

        return true;
    }

    private void Append(ReadOnlySpan<char> segment)
    {
        if (_lineTooLong)
        {
            return;
        }

        if (_line.Length + segment.Length > maxLineLength)
        {
            _lineTooLong = true;

            _line.Clear();

            return;
        }

        _line.Append(segment);
    }

    private BoundedLine CompleteLine() =>
        _lineTooLong
            ? new BoundedLine(string.Empty, TooLong: true)
            : new BoundedLine(_line.ToString(), TooLong: false);
}
