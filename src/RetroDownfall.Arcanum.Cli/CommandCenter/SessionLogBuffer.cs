using System.Collections.ObjectModel;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum SessionLogEntryKind
{
    Status,
    User,
    Assistant,
    Tool,
    Command,
    Error,
    Dashboard,
}

internal sealed class SessionLogEntry
{
    public SessionLogEntry(SessionLogEntryKind kind, string text, bool streaming = false)
    {
        Kind = kind;
        Text = text ?? string.Empty;
        Streaming = streaming;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public SessionLogEntryKind Kind { get; }

    public string Text { get; set; }

    public bool Streaming { get; set; }

    public DateTimeOffset CreatedUtc { get; }
}

/// <summary>
/// Bounded session log for the Command Center main pane.
/// </summary>
internal sealed class SessionLogBuffer
{
    public const int DefaultMaxEntries = 1000;

    public const int DefaultMaxCommandChars = 16_384;

    public const int DefaultMaxToolChars = 4_096;

    public const int DefaultMaxAssistantChars = 200_000;

    public const string TruncationMarker = "\n… [truncated]";

    /// <summary>API placeholder that must not linger in the scrollback.</summary>
    public const string GeneratingStatusMessage = "Mage is generating response...";

    private readonly object _gate = new();

    private readonly List<SessionLogEntry> _entries = new();

    public SessionLogBuffer(
        int maxEntries = DefaultMaxEntries,
        int maxCommandChars = DefaultMaxCommandChars,
        int maxToolChars = DefaultMaxToolChars,
        int maxAssistantChars = DefaultMaxAssistantChars)
    {
        MaxEntries = Math.Max(1, maxEntries);
        MaxCommandChars = Math.Max(64, maxCommandChars);
        MaxToolChars = Math.Max(64, maxToolChars);
        MaxAssistantChars = Math.Max(256, maxAssistantChars);
    }

    public int MaxEntries { get; }

    public int MaxCommandChars { get; }

    public int MaxToolChars { get; }

    public int MaxAssistantChars { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public static bool IsEphemeralGeneratingStatus(string? message) =>
        string.Equals(message?.Trim(), GeneratingStatusMessage, StringComparison.OrdinalIgnoreCase)
        || string.Equals(message?.Trim(), "generating…", StringComparison.OrdinalIgnoreCase)
        || string.Equals(message?.Trim(), "generating...", StringComparison.OrdinalIgnoreCase);

    public SessionLogEntry Append(SessionLogEntryKind kind, string text, bool streaming = false)
    {
        string clamped = ClampForKind(kind, text ?? string.Empty);
        SessionLogEntry entry = new(kind, clamped, streaming);

        lock (_gate)
        {
            _entries.Add(entry);
            TrimUnlocked();
        }

        return entry;
    }

    public void UpdateStreaming(SessionLogEntry entry, string text)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            entry.Text = ClampForKind(entry.Kind, text ?? string.Empty);
        }
    }

    public void CompleteStreaming(SessionLogEntry entry, string? finalText = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (finalText is not null)
            {
                entry.Text = ClampForKind(entry.Kind, finalText);
            }

            entry.Streaming = false;
        }
    }

    /// <summary>Drops stuck "Mage is generating…" status lines from the scrollback.</summary>
    public int RemoveEphemeralGeneratingStatuses()
    {
        lock (_gate)
        {
            return _entries.RemoveAll(static e =>
                e.Kind == SessionLogEntryKind.Status && IsEphemeralGeneratingStatus(e.Text));
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public const string OlderMessagesMarker = "Older messages not loaded";

    public const string EmptySessionMessage = "No messages in this session yet.";

    /// <summary>
    /// Replaces the transcript with loaded history (already chronological).
    /// Optionally prepends an older-messages marker.
    /// </summary>
    public void ReplaceWithHistory(
        IReadOnlyList<(SessionLogEntryKind Kind, string Text)> entries,
        bool showOlderMessagesMarker)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _entries.Clear();

            if (showOlderMessagesMarker)
            {
                _entries.Add(new SessionLogEntry(SessionLogEntryKind.Status, OlderMessagesMarker));
            }

            if (entries.Count == 0)
            {
                _entries.Add(new SessionLogEntry(SessionLogEntryKind.Status, EmptySessionMessage));
            }
            else
            {
                foreach ((SessionLogEntryKind kind, string text) in entries)
                {
                    _entries.Add(new SessionLogEntry(kind, ClampForKind(kind, text ?? string.Empty)));
                }
            }

            TrimUnlocked();
        }
    }

    public static SessionLogEntryKind MapEntryRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return SessionLogEntryKind.Status;
        }

        if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            return SessionLogEntryKind.User;
        }

        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            || role.Equals("mage", StringComparison.OrdinalIgnoreCase))
        {
            return SessionLogEntryKind.Assistant;
        }

        if (role.Equals("tool", StringComparison.OrdinalIgnoreCase)
            || role.Equals("function", StringComparison.OrdinalIgnoreCase))
        {
            return SessionLogEntryKind.Tool;
        }

        if (role.Equals("system", StringComparison.OrdinalIgnoreCase)
            || role.Equals("ward", StringComparison.OrdinalIgnoreCase))
        {
            return SessionLogEntryKind.Status;
        }

        return SessionLogEntryKind.Status;
    }

    public IReadOnlyList<SessionLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public string RenderPlainText()
    {
        lock (_gate)
        {
            return string.Join(
                Environment.NewLine + Environment.NewLine,
                _entries.Select(static e => FormatEntry(e)));
        }
    }

    public void CopyLinesTo(ObservableCollection<string> target, int wrapWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(target);

        string[] lines;
        lock (_gate)
        {
            // ListView is one row per item — expand embedded newlines and soft-wrap to pane width.
            IEnumerable<string> raw = _entries
                .SelectMany(static e => FormatEntry(e).Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n'));

            lines = wrapWidth > 1
                ? raw.SelectMany(line => WrapLine(line, wrapWidth)).ToArray()
                : raw.ToArray();
        }

        target.Clear();
        foreach (string line in lines)
        {
            target.Add(line);
        }
    }

    /// <summary>Soft-wrap a single line to <paramref name="width"/> columns (word-aware).</summary>
    public static IEnumerable<string> WrapLine(string line, int width)
    {
        if (width < 2)
        {
            yield return line;
            yield break;
        }

        if (string.IsNullOrEmpty(line))
        {
            yield return string.Empty;
            yield break;
        }

        // Fast path: already fits.
        if (line.Length <= width)
        {
            yield return line;
            yield break;
        }

        int index = 0;
        while (index < line.Length)
        {
            int remaining = line.Length - index;
            if (remaining <= width)
            {
                yield return line[index..];
                yield break;
            }

            int take = width;
            int sliceEnd = index + take;

            // Prefer breaking on whitespace within the window.
            int breakAt = -1;
            for (int i = sliceEnd - 1; i > index; i--)
            {
                if (char.IsWhiteSpace(line[i]))
                {
                    breakAt = i;
                    break;
                }
            }

            if (breakAt > index)
            {
                yield return line[index..breakAt].TrimEnd();
                index = breakAt + 1;
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                {
                    index++;
                }
            }
            else
            {
                // Hard break for unbroken tokens longer than width.
                yield return line[index..sliceEnd];
                index = sliceEnd;
            }
        }
    }

    private static string FormatEntry(SessionLogEntry entry)
    {
        string prefix = entry.Kind switch
        {
            SessionLogEntryKind.User => "You: ",
            SessionLogEntryKind.Assistant => entry.Streaming
                ? (string.IsNullOrEmpty(entry.Text) ? "Mage is generating…" : "Mage (streaming): ")
                : "Mage: ",
            SessionLogEntryKind.Tool => "Tool: ",
            SessionLogEntryKind.Command => "",
            SessionLogEntryKind.Error => "Error: ",
            SessionLogEntryKind.Dashboard => "",
            _ => "",
        };

        return prefix + entry.Text;
    }

    private string ClampForKind(SessionLogEntryKind kind, string text)
    {
        int max = kind switch
        {
            SessionLogEntryKind.Command => MaxCommandChars,
            SessionLogEntryKind.Tool => MaxToolChars,
            SessionLogEntryKind.Assistant => MaxAssistantChars,
            SessionLogEntryKind.User => MaxAssistantChars,
            _ => MaxCommandChars,
        };

        if (text.Length <= max)
        {
            return text;
        }

        int keep = Math.Max(0, max - TruncationMarker.Length);
        return text[..keep] + TruncationMarker;
    }

    private void TrimUnlocked()
    {
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }
}
