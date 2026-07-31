using System.Runtime.CompilerServices;
using RetroDownfall.TheForge.Core.Models;

namespace RetroDownfall.TheForge.Ux.Services;

/// <summary>
/// Parses Server-Sent-Events framing from a line-oriented <see cref="TextReader"/>: accumulates
/// <c>data:</c> lines into one <see cref="SseEvent"/> per blank-line boundary, captures the optional
/// <c>event:</c> line, skips <c>:</c> keep-alive comments, and stops at <c>data: [DONE]</c>.
///
/// Extracted from <see cref="ArcanumApiClient"/> so the framing logic is unit-testable against a
/// plain <see cref="StringReader"/> without an HTTP round-trip.
/// </summary>
internal static class SseFrameParser
{
    internal const int MaxLineChars = BoundedTextLineReader.DefaultMaxLineChars;

    internal const int MaxEventChars = 8 * 1024 * 1024;

    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        string? eventName = null;

        List<string> dataLines = [];

        int dataChars = 0;

        bool discardEvent = false;

        BoundedTextLineReader lineReader = new(reader, MaxLineChars);

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            BoundedTextLineReadResult read = await lineReader
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!read.HasLine)
            {

                break;

            }

            if (read.IsTooLong)
            {
                discardEvent = true;
                dataLines.Clear();
                dataChars = 0;
                eventName = null;

                continue;
            }

            string line = read.Line;

            if (line.Length == 0)
            {
                if (discardEvent)
                {
                    discardEvent = false;
                    dataLines.Clear();
                    dataChars = 0;
                    eventName = null;

                    continue;
                }

                if (dataLines.Count > 0)
                {

                    string data = string.Join('\n', dataLines);

                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    {

                        yield break;

                    }

                    yield return new SseEvent(eventName, data);

                    dataLines.Clear();

                    dataChars = 0;

                    eventName = null;

                }

                continue;

            }

            if (discardEvent)
            {
                continue;
            }

            if (line.StartsWith(':'))
            {

                // Keep-alive comment — ignore.
                continue;

            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {

                eventName = line["event:".Length..].Trim();

            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {

                string dataLine = line["data:".Length..].TrimStart();

                dataChars += dataLine.Length + (dataLines.Count > 0 ? 1 : 0);

                if (dataChars > MaxEventChars)
                {
                    discardEvent = true;
                    dataLines.Clear();
                    dataChars = 0;
                    eventName = null;

                    continue;
                }

                dataLines.Add(dataLine);

            }

        }

        if (!discardEvent && dataLines.Count > 0)
        {

            string data = string.Join('\n', dataLines);

            if (!string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {

                yield return new SseEvent(eventName, data);

            }

        }

    }

}
