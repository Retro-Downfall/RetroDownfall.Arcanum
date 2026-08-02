using System.Buffers;

using System.Runtime.CompilerServices;

using System.Text.Json;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.Services;

internal static class WatchSseParser
{

    internal static readonly TimeSpan IdleDiagnosticInterval = TimeSpan.FromSeconds(30);

    private static readonly Error InvalidJsonError = new(
        "Api.InvalidResponse",
        "Malformed or non-object JSON event received from the API.");

    internal static IAsyncEnumerable<WatchSseFrame> ParseAsync(
        TextReader reader,
        CancellationToken cancellationToken) =>
        ParseAsync(
            reader,
            IdleDiagnosticInterval,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    internal static async IAsyncEnumerable<WatchSseFrame> ParseAsync(
        TextReader reader,
        TimeSpan idleDiagnosticInterval,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(reader);

        ArgumentNullException.ThrowIfNull(delayAsync);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            idleDiagnosticInterval,
            TimeSpan.Zero);

        List<string> dataLines = [];

        string? eventName = null;

        Task<string?>? pendingRead = null;

        while (true)
        {

            cancellationToken.ThrowIfCancellationRequested();

            pendingRead ??= reader
                .ReadLineAsync(cancellationToken)
                .AsTask();

            using CancellationTokenSource delayCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            Task idleDelay = delayAsync(
                idleDiagnosticInterval,
                delayCts.Token);

            Task completed = await Task
                .WhenAny(pendingRead, idleDelay)
                .ConfigureAwait(false);

            if (completed == idleDelay)
            {

                await idleDelay.ConfigureAwait(false);

                yield return new WatchSseFrame(
                    WatchSseFrameType.Heartbeat,
                    Diagnostic: "No stream activity observed; still waiting for the next frame.");

                continue;

            }

            delayCts.Cancel();

            string? line = await pendingRead
                .ConfigureAwait(false);

            pendingRead = null;

            if (line is null)
            {

                if (dataLines.Count > 0)
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

            if (line.Length == 0)
            {

                if (dataLines.Count == 0)
                {

                    eventName = null;

                    continue;

                }

                WatchSseFrame frame = CreateEventFrame(eventName, dataLines);

                ResetEvent(dataLines, ref eventName);

                yield return frame;

                if (frame.Type == WatchSseFrameType.Done)
                {

                    yield break;

                }

                continue;

            }

            if (line[0] == ':')
            {

                yield return new WatchSseFrame(
                    WatchSseFrameType.Heartbeat,
                    Diagnostic: "Server keep-alive received; still waiting for source events.");

                continue;

            }

            int separator = line.IndexOf(':');

            string field = separator < 0
                ? line
                : line[..separator];

            string value = separator < 0
                ? string.Empty
                : line[(separator + 1)..];

            if (value.StartsWith(' '))
            {

                value = value[1..];

            }

            if (string.Equals(field, "event", StringComparison.Ordinal))
            {

                eventName = value;

                continue;

            }

            if (!string.Equals(field, "data", StringComparison.Ordinal))
            {

                continue;

            }

            dataLines.Add(value);

        }

    }

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
        ref string? eventName)
    {

        dataLines.Clear();

        eventName = null;

    }

}
