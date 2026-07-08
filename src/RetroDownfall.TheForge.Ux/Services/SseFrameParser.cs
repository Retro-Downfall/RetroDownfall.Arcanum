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

    public static async IAsyncEnumerable<SseEvent> ParseAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        string? eventName = null;

        List<string> dataLines = [];

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {

                break;

            }

            if (line.Length == 0)
            {

                if (dataLines.Count > 0)
                {

                    string data = string.Join('\n', dataLines);

                    if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    {

                        yield break;

                    }

                    yield return new SseEvent(eventName, data);

                    dataLines.Clear();

                    eventName = null;

                }

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

                dataLines.Add(line["data:".Length..].TrimStart());

            }

        }

        if (dataLines.Count > 0)
        {

            string data = string.Join('\n', dataLines);

            if (!string.Equals(data, "[DONE]", StringComparison.Ordinal))
            {

                yield return new SseEvent(eventName, data);

            }

        }

    }

}
