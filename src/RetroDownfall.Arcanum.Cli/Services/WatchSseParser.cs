using System.Buffers;

using System.Runtime.CompilerServices;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

internal static class WatchSseParser
{
    internal static readonly TimeSpan IdleDiagnosticInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most characters a single line, or the data of one event joined, may hold before the event is
    /// discarded. It is a runaway guard, not a payload policy: the server's own ceiling on one tool
    /// output is 64 MiB, so nothing the server is allowed to send reaches it, while a stream that never
    /// ends a line cannot grow the client without bound.
    /// </summary>
    internal const int DefaultMaxEventLength = 64 * 1024 * 1024;

    private static readonly Error InvalidJsonError = new(
        "Api.InvalidResponse",
        "Malformed or non-object JSON event received from the API.");

    private static readonly Error OversizedEventError = new(
        "Api.InvalidResponse",
        "An event larger than the client limit was received and discarded.");

    internal static IAsyncEnumerable<WatchSseFrame> ParseAsync(
        TextReader reader,
        CancellationToken cancellationToken) =>
        ParseAsync(
            reader,
            IdleDiagnosticInterval,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    internal static IAsyncEnumerable<WatchSseFrame> ParseAsync(
        TextReader reader,
        TimeSpan idleDiagnosticInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken) =>
        ParseAsync(
            reader,
            idleDiagnosticInterval,
            delayAsync,
            DefaultMaxEventLength,
            cancellationToken);

    /// <summary>
    /// Parses the stream into frames. The idle diagnostic is armed once per window rather than once per
    /// line: a window that ends after lines arrived inside it was not idle for its whole length, so it
    /// reports nothing and the next window starts. The diagnostic therefore appears after between one
    /// and two intervals of silence, which is the price of not building a timer for every line.
    /// </summary>
    internal static async IAsyncEnumerable<WatchSseFrame> ParseAsync(
        TextReader reader,
        TimeSpan idleDiagnosticInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        int maxEventLength,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        ArgumentNullException.ThrowIfNull(delayAsync);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            idleDiagnosticInterval,
            TimeSpan.Zero);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEventLength);

        BoundedLineReader lineReader = new(reader, maxEventLength);

        List<string> dataLines = [];

        long dataLength = 0;

        bool oversized = false;

        string? eventName = null;

        Task<SseLine?>? pendingRead = null;

        CancellationTokenSource? idleWindow = null;

        Task? idleDelay = null;

        long activity = 0;

        long activityAtWindowStart = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                pendingRead ??= lineReader.ReadLineAsync(cancellationToken);

                if (idleDelay is null)
                {
                    idleWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    activityAtWindowStart = activity;

                    idleDelay = delayAsync(
                        idleDiagnosticInterval,
                        idleWindow.Token);
                }

                Task completed = await Task
                    .WhenAny(pendingRead, idleDelay)
                    .ConfigureAwait(false);

                if (completed == idleDelay)
                {
                    await idleDelay.ConfigureAwait(false);

                    bool idleForTheWholeWindow = activity == activityAtWindowStart;

                    idleWindow!.Dispose();

                    idleWindow = null;

                    idleDelay = null;

                    if (idleForTheWholeWindow)
                    {
                        yield return new WatchSseFrame(
                            WatchSseFrameType.Heartbeat,
                            Diagnostic: "No stream activity observed; still waiting for the next frame.");
                    }

                    continue;
                }

                SseLine? read = await pendingRead
                    .ConfigureAwait(false);

                pendingRead = null;

                activity++;

                if (read is not { } line)
                {
                    if (oversized)
                    {
                        yield return CreateOversizedFrame();
                    }
                    else if (dataLines.Count > 0)
                    {
                        WatchSseFrame finalFrame = CreateEventFrame(
                            eventName,
                            dataLines);

                        yield return finalFrame;

                        if (finalFrame.Type == WatchSseFrameType.Done)
                        {
                            yield break;
                        }
                    }

                    yield return new WatchSseFrame(
                        WatchSseFrameType.UnexpectedEof,
                        Diagnostic: "The stream disconnected before a [DONE] marker was received.",
                        Retryable: true);

                    yield break;
                }

                if (line.TooLong)
                {
                    // The text was never kept: the whole event is lost, and the frame saying so is
                    // emitted at its blank line like any other malformed event.
                    oversized = true;

                    dataLines.Clear();

                    dataLength = 0;

                    continue;
                }

                string text = line.Text;

                if (text.Length == 0)
                {
                    if (oversized)
                    {
                        ResetEvent(dataLines, ref eventName, ref dataLength, ref oversized);

                        yield return CreateOversizedFrame();

                        continue;
                    }

                    if (dataLines.Count == 0)
                    {
                        eventName = null;

                        continue;
                    }

                    WatchSseFrame frame = CreateEventFrame(eventName, dataLines);

                    ResetEvent(dataLines, ref eventName, ref dataLength, ref oversized);

                    yield return frame;

                    if (frame.Type == WatchSseFrameType.Done)
                    {
                        yield break;
                    }

                    continue;
                }

                if (text[0] == ':')
                {
                    yield return new WatchSseFrame(
                        WatchSseFrameType.Heartbeat,
                        Diagnostic: "Server keep-alive received; still waiting for source events.");

                    continue;
                }

                int separator = text.IndexOf(':');

                string field = separator < 0
                    ? text
                    : text[..separator];

                string value = separator < 0
                    ? string.Empty
                    : text[(separator + 1)..];

                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                if (string.Equals(field, "event", StringComparison.Ordinal))
                {
                    eventName = value;

                    continue;
                }

                if (!string.Equals(field, "data", StringComparison.Ordinal)
                    || oversized)
                {
                    continue;
                }

                // The joined payload is the data lines with one separator between each pair.
                dataLength += value.Length + (dataLines.Count > 0 ? 1 : 0);

                if (dataLength > maxEventLength)
                {
                    oversized = true;

                    dataLines.Clear();

                    continue;
                }

                dataLines.Add(value);
            }
        }
        finally
        {
            idleWindow?.Cancel();

            idleWindow?.Dispose();
        }
    }

    private static WatchSseFrame CreateOversizedFrame() =>
        new(
            WatchSseFrameType.Error,
            Error: OversizedEventError,
            Recoverable: true);

    private static WatchSseFrame CreateEventFrame(
        string? eventName,
        List<string> dataLines)
    {
        string rawJson = string.Join('\n', dataLines);

        if (string.Equals(rawJson, "[DONE]", StringComparison.Ordinal))
        {
            return new WatchSseFrame(WatchSseFrameType.Done);
        }

        JsonElement data;

        try
        {
            using JsonDocument document = JsonDocument.Parse(rawJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new WatchSseFrame(
                    WatchSseFrameType.Error,
                    Error: InvalidJsonError,
                    Recoverable: true);
            }

            ArrayBufferWriter<byte> normalizedBuffer = new();

            using (Utf8JsonWriter writer = new(normalizedBuffer))
            {
                document.RootElement.WriteTo(writer);
            }

            using JsonDocument normalized = JsonDocument.Parse(
                normalizedBuffer.WrittenMemory);

            data = normalized.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new WatchSseFrame(
                WatchSseFrameType.Error,
                Error: InvalidJsonError,
                Recoverable: true);
        }

        if (IsControlFrame(eventName, data, out string diagnostic))
        {
            return new WatchSseFrame(
                WatchSseFrameType.Heartbeat,
                RawJson: rawJson,
                Diagnostic: diagnostic);
        }

        return new WatchSseFrame(
            WatchSseFrameType.Data,
            Data: data,
            RawJson: rawJson);
    }

    private static bool IsControlFrame(
        string? eventName,
        JsonElement data,
        out string diagnostic)
    {
        if (IsControlName(eventName))
        {
            diagnostic = $"Server control frame received: {eventName}.";

            return true;
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("type", out JsonElement type)
            && type.ValueKind == JsonValueKind.String
            && IsControlName(type.GetString()))
        {
            diagnostic = $"Server control frame received: {type.GetString()}.";

            return true;
        }

        if (IsConnectionAcknowledgement(data))
        {
            diagnostic = "Server connection acknowledgement received.";

            return true;
        }

        diagnostic = string.Empty;

        return false;
    }

    private static bool IsConnectionAcknowledgement(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement.ObjectEnumerator properties = data.EnumerateObject();

        if (!properties.MoveNext())
        {
            return false;
        }

        JsonProperty property = properties.Current;

        return string.Equals(
                property.Name,
                "connected",
                StringComparison.Ordinal)
            && property.Value.ValueKind == JsonValueKind.True
            && !properties.MoveNext();
    }

    private static bool IsControlName(string? value) =>
        string.Equals(value, "live", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "heartbeat", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "keep-alive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "keepalive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "control", StringComparison.OrdinalIgnoreCase);

    private static void ResetEvent(
        List<string> dataLines,
        ref string? eventName,
        ref long dataLength,
        ref bool oversized)
    {
        dataLines.Clear();

        eventName = null;

        dataLength = 0;

        oversized = false;
    }

    /// <summary>
    /// One line of the stream. A line over the limit is not kept: <see cref="TooLong"/> says it existed.
    /// </summary>
    private readonly record struct SseLine(string Text, bool TooLong);

    /// <summary>
    /// Splits a character stream into lines on LF, CR or CRLF, the three terminators the SSE format
    /// allows, without ever holding more than <c>maxLineLength</c> characters of one line. A line is
    /// complete the moment its terminator arrives; a LF that follows a CR in a later read is swallowed
    /// rather than read as an empty line.
    /// </summary>
    private sealed class BoundedLineReader(TextReader reader, int maxLineLength)
    {
        private readonly char[] _buffer = new char[4096];

        private readonly StringBuilder _line = new();

        private int _start;

        private int _end;

        private bool _skipLineFeed;

        private bool _lineTooLong;

        public async Task<SseLine?> ReadLineAsync(CancellationToken cancellationToken)
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

                if (TryConsumeLine(out SseLine line))
                {
                    return line;
                }
            }
        }

        private bool TryConsumeLine(out SseLine line)
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
                line = new SseLine(new string(segment), TooLong: false);

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

        private SseLine CompleteLine() =>
            _lineTooLong
                ? new SseLine(string.Empty, TooLong: true)
                : new SseLine(_line.ToString(), TooLong: false);
    }
}
