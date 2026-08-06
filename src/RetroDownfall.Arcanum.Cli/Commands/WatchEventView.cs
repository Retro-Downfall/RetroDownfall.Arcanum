using System.Globalization;

using System.Text.Json;

namespace RetroDownfall.Arcanum.Cli.Commands;

internal enum WatchSource
{

    Session,

    Apprentice,

    Logs,

    Mcp,

    Daemons,

}

internal sealed record WatchEventView(
    DateTimeOffset Timestamp,
    string EventType,
    string Message,
    string[] ToolNames,
    bool IsFailure,
    bool IsActive)
{

    public static WatchEventView Create(
        WatchSource source,
        JsonElement payload,
        DateTimeOffset observedAt)
    {

        string eventType = source switch
        {

            WatchSource.Session => String(payload, "role") ?? "entry",

            WatchSource.Apprentice => String(payload, "type") ?? "unknown",

            WatchSource.Logs => String(payload, "level") ?? "log",

            WatchSource.Mcp => String(payload, "state") ?? "mcp",

            WatchSource.Daemons => String(payload, "eventType") ?? "daemon",

            _ => "event",

        };

        DateTimeOffset timestamp = ParseTimestamp(payload, "timestamp")
            ?? ParseTimestamp(payload, "createdAt")
            ?? observedAt;

        string message = source switch
        {

            WatchSource.Session => Join(
                String(payload, "role"),
                String(payload, "content")),

            WatchSource.Apprentice => ApprenticeMessage(payload, eventType),

            WatchSource.Logs => Join(
                String(payload, "category"),
                String(payload, "message")),

            WatchSource.Mcp => Join(
                String(payload, "serverName"),
                String(payload, "message") ?? eventType),

            WatchSource.Daemons => Join(
                String(payload, "jobName"),
                String(payload, "message")
                    ?? String(payload, "targetSpell")
                    ?? eventType),

            _ => eventType,

        };

        string normalizedType = eventType.ToLowerInvariant();

        bool failure = normalizedType.Contains("fail", StringComparison.Ordinal)
            || normalizedType.Contains("error", StringComparison.Ordinal)
            || normalizedType.Contains("critical", StringComparison.Ordinal)
            || normalizedType.Contains("unhealthy", StringComparison.Ordinal)
            || normalizedType.Contains("warning", StringComparison.Ordinal)
            || normalizedType.Contains("dropped", StringComparison.Ordinal);

        bool active = normalizedType.Contains("start", StringComparison.Ordinal)
            || normalizedType.Contains("running", StringComparison.Ordinal)
            || normalizedType.Contains("information", StringComparison.Ordinal)
            || normalizedType.Contains("debug", StringComparison.Ordinal)
            || normalizedType.Contains("trace", StringComparison.Ordinal)
            || normalizedType.Contains("healthy", StringComparison.Ordinal);

        return new WatchEventView(
            timestamp,
            eventType,
            OneLine(message),
            ExtractToolNames(payload),
            failure,
            active);

    }

    public bool Matches(WatchCommandOptions options)
    {

        bool typeMatches = options.EventTypes.Length == 0
            || options.EventTypes.Any(
                candidate => string.Equals(
                    candidate?.Trim(),
                    EventType,
                    StringComparison.OrdinalIgnoreCase));

        if (!typeMatches)
        {

            return false;

        }

        return options.ToolNames.Length == 0
            || options.ToolNames.Any(
                candidate => ToolNames.Any(
                    actual => string.Equals(
                        candidate?.Trim(),
                        actual,
                        StringComparison.OrdinalIgnoreCase)));

    }

    public string TimestampText() =>
        Timestamp
            .ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string ApprenticeMessage(JsonElement payload, string eventType)
    {

        if (string.Equals(eventType, "eventsDropped", StringComparison.OrdinalIgnoreCase))
        {

            return "Some Chronicle events were dropped because the reader was slow.";

        }

        if (SendingMessage(payload, eventType) is { } sending)
        {

            return sending;

        }

        return String(payload, "message")
            ?? String(payload, "description")
            ?? String(payload, "result")
            ?? String(payload, "error")
            ?? String(payload, "summary")
            ?? eventType;

    }

    /// <summary>
    /// Renders the four A2A Sending frames distinguishably.
    /// </summary>
    /// <remarks>
    /// The generic fallback above reads <c>description</c> before <c>result</c>/<c>error</c>, and every
    /// Sending frame carries the agent URL in <c>description</c> — so dispatched, progress, completed, and
    /// failed all rendered as the same line and neither the response nor the failure reason was ever
    /// visible (issue #61).
    /// </remarks>
    private static string? SendingMessage(JsonElement payload, string eventType)
    {

        string agent = String(payload, "description") ?? "(unknown agent)";

        string task = String(payload, "summary") is { Length: > 0 } id ? $" task {id}" : string.Empty;

        switch (eventType)
        {

            case "sendingDispatched":

                return $"→ {agent}{task}: dispatched";

            case "sendingProgress":

                string state = String(payload, "sendingState") ?? "working";

                return $"… {agent}{task}: {state}";

            case "sendingCompleted":

                string response = String(payload, "result") ?? "(no response text)";

                return $"✓ {agent}{task}: completed [{SendingCost(payload)}]{Elapsed(payload)} — {response}";

            case "sendingFailed":

                string reason = String(payload, "error") ?? "(no reason reported)";

                return $"✗ {agent}{task}: failed [{SendingCost(payload)}]{Elapsed(payload)} — {reason}";

            default:

                return null;

        }

    }

    /// <summary>
    /// Keeps "the peer reported nothing" visibly distinct from "the peer reported zero" (issue #60).
    /// </summary>
    private static string SendingCost(JsonElement payload)
    {

        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("remoteCostKnown", out JsonElement known)
            || known.ValueKind != JsonValueKind.True)
        {

            return "cost unknown";

        }

        string tokens = Number(payload, "remoteTotalTokens") is { } t
            ? $"{t:N0} tokens"
            : "tokens unknown";

        string cost = Number(payload, "remoteCostUsd") is { } c
            ? $", ${c:0.######}"
            : string.Empty;

        return $"external {tokens}{cost}";

    }

    private static string Elapsed(JsonElement payload) =>
        Number(payload, "durationMs") is { } ms ? $" in {ms / 1000m:0.###} s" : string.Empty;

    private static decimal? Number(JsonElement payload, string property) =>
        payload.ValueKind == JsonValueKind.Object
        && payload.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out decimal parsed)
            ? parsed
            : null;

    private static string[] ExtractToolNames(JsonElement payload)
    {

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        AddString(payload, "toolName", names);

        if (payload.TryGetProperty("tools", out JsonElement tools)
            && tools.ValueKind == JsonValueKind.Array)
        {

            foreach (JsonElement tool in tools.EnumerateArray())
            {

                if (tool.ValueKind == JsonValueKind.String
                    && tool.GetString() is { Length: > 0 } name)
                {

                    AddToolName(name, names);

                }

            }

        }

        if (payload.TryGetProperty("toolCall", out JsonElement toolCall)
            && toolCall.ValueKind == JsonValueKind.Object)
        {

            AddString(toolCall, "name", names);

            AddString(toolCall, "toolName", names);

        }

        if (payload.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object)
        {

            foreach (JsonProperty property in properties.EnumerateObject())
            {

                if ((string.Equals(
                            property.Name,
                            "toolName",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            property.Name,
                            "tool",
                            StringComparison.OrdinalIgnoreCase))
                    && property.Value.ValueKind == JsonValueKind.String)
                {

                    AddToolName(property.Value.GetString(), names);

                }

            }

        }

        return names.ToArray();

    }

    private static void AddString(
        JsonElement payload,
        string property,
        HashSet<string> values)
    {

        if (String(payload, property) is { Length: > 0 } value)
        {

            AddToolName(value, values);

        }

    }

    private static void AddToolName(
        string? rawValue,
        HashSet<string> values)
    {

        string value = rawValue?.Trim() ?? string.Empty;

        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {

            value = value[1..^1].Trim();

        }

        if (value.Length > 0)
        {

            values.Add(value);

        }

    }

    private static DateTimeOffset? ParseTimestamp(JsonElement payload, string property)
    {

        string? value = String(payload, property);

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset timestamp)
                ? timestamp
                : null;

    }

    private static string? String(JsonElement payload, string property)
    {

        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    }

    private static string Join(string? label, string? body)
    {

        if (string.IsNullOrWhiteSpace(label))
        {

            return body ?? string.Empty;

        }

        return string.IsNullOrWhiteSpace(body)
            ? label
            : $"{label}: {body}";

    }

    private static string OneLine(string value) =>
        value
            .Replace('\r', ' ')
            .Replace('\n', ' ');

}
