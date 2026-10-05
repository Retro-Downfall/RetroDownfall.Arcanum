using System.Collections.ObjectModel;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum SessionLogEntryKind
{
    Status,
    User,
    Reasoning,
    Assistant,
    Tool,
    Command,
    Error,
    Dashboard,
}

internal sealed class SessionLogEntry
{
    public SessionLogEntry(
        SessionLogEntryKind kind,
        string text,
        bool streaming = false,
        Guid? sourceEntryId = null)
    {
        Id = Guid.NewGuid();
        Kind = kind;
        Text = text ?? string.Empty;
        Streaming = streaming;
        CreatedUtc = DateTimeOffset.UtcNow;
        SourceEntryId = sourceEntryId;
    }

    public Guid Id { get; }

    public SessionLogEntryKind Kind { get; }

    public string Text { get; set; }

    public bool Streaming { get; set; }

    public DateTimeOffset CreatedUtc { get; }

    public Guid? SourceEntryId { get; }
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

    public const int DefaultMaxReasoningChars = 64 * 1024;

    public const string TruncationMarker = "\n… [truncated]";

    public const string ReasoningTruncationMarker = "\n… [reasoning truncated]";

    /// <summary>API placeholder that must not linger in the scrollback.</summary>
    public const string GeneratingStatusMessage = "Mage is generating response...";

    private readonly object _gate = new();

    private readonly List<SessionLogEntry> _entries = new();

    /// <summary>
    /// Wrapped display lines per entry, keyed by entry id and invalidated by the exact text instance,
    /// streaming flag and wrap width. A streaming flush therefore re-wraps only the entry whose text
    /// actually changed instead of re-formatting the whole transcript at 20 frames per second.
    /// </summary>
    private readonly Dictionary<Guid, WrappedEntryLines> _wrapCache = new();

    public SessionLogBuffer(
        int maxEntries = DefaultMaxEntries,
        int maxCommandChars = DefaultMaxCommandChars,
        int maxToolChars = DefaultMaxToolChars,
        int maxAssistantChars = DefaultMaxAssistantChars,
        int maxReasoningChars = DefaultMaxReasoningChars)
    {
        MaxEntries = Math.Max(1, maxEntries);
        MaxCommandChars = Math.Max(64, maxCommandChars);
        MaxToolChars = Math.Max(64, maxToolChars);
        MaxAssistantChars = Math.Max(256, maxAssistantChars);
        MaxReasoningChars = Math.Max(64, maxReasoningChars);
    }

    public int MaxEntries { get; }

    public int MaxCommandChars { get; }

    public int MaxToolChars { get; }

    public int MaxAssistantChars { get; }

    public int MaxReasoningChars { get; }

    /// <summary>
    /// How many newline-delimited segments have been wrapped over this buffer's lifetime. A diagnostic
    /// that lets a test prove a streaming flush wraps only what is new instead of the whole answer.
    /// </summary>
    internal long WrappedSegmentCount { get; private set; }

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
        string clamped = SanitizeAndClamp(kind, text ?? string.Empty);
        SessionLogEntry entry = new(kind, clamped, streaming);

        lock (_gate)
        {
            _entries.Add(entry);
            TrimUnlocked();
        }

        return entry;
    }

    public SessionLogEntry InsertBefore(
        SessionLogEntry before,
        SessionLogEntryKind kind,
        string text,
        bool streaming = false)
    {
        ArgumentNullException.ThrowIfNull(before);
        SessionLogEntry entry = new(kind, SanitizeAndClamp(kind, text ?? string.Empty), streaming);

        lock (_gate)
        {
            int index = _entries.FindIndex(candidate => ReferenceEquals(candidate, before));
            if (index < 0)
            {
                throw new ArgumentException("The anchor entry does not belong to this log.", nameof(before));
            }

            _entries.Insert(index, entry);
            TrimUnlocked();
        }

        return entry;
    }

    public void UpdateStreaming(SessionLogEntry entry, string text)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            entry.Text = SanitizeAndClamp(entry.Kind, text ?? string.Empty);
        }
    }

    public void CompleteStreaming(SessionLogEntry entry, string? finalText = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (finalText is not null)
            {
                entry.Text = SanitizeAndClamp(entry.Kind, finalText);
            }

            entry.Streaming = false;
        }
    }

    /// <summary>Drops stuck "Mage is generating…" status lines from the scrollback.</summary>
    public int RemoveEphemeralGeneratingStatuses()
    {
        lock (_gate)
        {
            int removed = _entries.RemoveAll(static e =>
                e.Kind == SessionLogEntryKind.Status && IsEphemeralGeneratingStatus(e.Text));
            PruneWrapCacheUnlocked();

            return removed;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _wrapCache.Clear();
        }
    }

    public const string OlderMessagesMarker = "Older messages not loaded";

    public const string NewerMessagesMarker = "Newer messages not loaded";

    public const string EmptySessionMessage = "No messages in this session yet.";

    /// <summary>
    /// Replaces the transcript with loaded history (already chronological).
    /// Optionally prepends an older-messages marker.
    /// </summary>
    public void ReplaceWithHistory(
        IReadOnlyList<(SessionLogEntryKind Kind, string Text)> entries,
        bool showOlderMessagesMarker)
        => ReplaceWithApiHistory(
            entries.Select(static entry => (entry.Kind, entry.Text, (Guid?)null)).ToArray(),
            showOlderMessagesMarker);

    public void ReplaceWithApiHistory(
        IReadOnlyList<(SessionLogEntryKind Kind, string Text, Guid? SourceEntryId)> entries,
        bool showOlderMessagesMarker,
        bool showNewerMessagesMarker = false)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (_gate)
        {
            _entries.Clear();
            _wrapCache.Clear();

            if (showOlderMessagesMarker)
            {
                _entries.Add(new SessionLogEntry(SessionLogEntryKind.Status, OlderMessagesMarker));
            }

            bool addedHistory = false;
            foreach ((SessionLogEntryKind kind, string text, Guid? sourceEntryId) in entries)
            {
                if (kind == SessionLogEntryKind.Reasoning)
                {
                    continue;
                }

                _entries.Add(new SessionLogEntry(
                    kind, SanitizeAndClamp(kind, text ?? string.Empty), sourceEntryId: sourceEntryId));
                addedHistory = true;
            }

            if (!addedHistory)
            {
                _entries.Add(new SessionLogEntry(SessionLogEntryKind.Status, EmptySessionMessage));
            }

            if (showNewerMessagesMarker)
            {
                _entries.Add(new SessionLogEntry(SessionLogEntryKind.Status, NewerMessagesMarker));
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

        if (role.Equals("reasoning", StringComparison.OrdinalIgnoreCase))
        {
            return SessionLogEntryKind.Reasoning;
        }

        if (role.Equals("system", StringComparison.OrdinalIgnoreCase)
            || role.Equals("ward", StringComparison.OrdinalIgnoreCase))
        {
            return SessionLogEntryKind.Status;
        }

        return SessionLogEntryKind.Status;
    }

    public static bool IsEphemeralReasoningRole(string? role) =>
        string.Equals(role?.Trim(), "reasoning", StringComparison.OrdinalIgnoreCase);

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

    public void CopyLinesTo(ObservableCollection<string> target, int wrapWidth = 0) =>
        CopyLinesTo(target, lineAnchors: null, wrapWidth);

    /// <summary>
    /// Copies transcript lines excluding <see cref="SessionLogEntryKind.Tool"/>.
    /// When <paramref name="lineAnchors"/> is provided, each line is tagged with the source entry id.
    /// Unchanged entries reuse their cached wrapped lines and <paramref name="target"/> is edited in
    /// place from the first differing line, so a streaming append costs work proportional to the
    /// appended text rather than to the whole transcript.
    /// </summary>
    public void CopyLinesTo(
        ObservableCollection<string> target,
        List<Guid?>? lineAnchors,
        int wrapWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(target);

        List<string> lines = new();
        List<Guid?> anchors = new();
        lock (_gate)
        {
            BuildDisplayLinesUnlocked(wrapWidth, lines, anchors);
        }

        _ = CommandCenterLineSync.ApplyTailEdit(target, lines);

        if (lineAnchors is not null)
        {
            lineAnchors.Clear();
            lineAnchors.AddRange(anchors);
        }
    }

    private void BuildDisplayLinesUnlocked(int wrapWidth, List<string> lines, List<Guid?> anchors)
    {
        bool emittedAnyEntry = false;
        bool previousBlank = false;
        foreach (SessionLogEntry e in _entries)
        {
            if (e.Kind == SessionLogEntryKind.Tool)
            {
                continue;
            }

            string[] entryLines = GetWrappedLinesUnlocked(e, wrapWidth);
            if (entryLines.Length == 0)
            {
                continue;
            }

            if (emittedAnyEntry && !previousBlank)
            {
                // One blank line between transcript entries.
                lines.Add(string.Empty);
                anchors.Add(e.Id);
                previousBlank = true;
            }

            foreach (string line in entryLines)
            {
                bool blank = line.Length == 0;
                if (blank && previousBlank)
                {
                    // At most one blank line between content (model/markdown often emits \n\n\n).
                    continue;
                }

                lines.Add(line);
                anchors.Add(e.Id);
                previousBlank = blank;
            }

            emittedAnyEntry = true;
        }
    }

    private string[] GetWrappedLinesUnlocked(SessionLogEntry entry, int wrapWidth)
    {
        if (_wrapCache.TryGetValue(entry.Id, out WrappedEntryLines? cached)
            && cached.WrapWidth == wrapWidth
            && cached.Streaming == entry.Streaming
            && ReferenceEquals(cached.SourceText, entry.Text))
        {
            return cached.Lines;
        }

        // Text only ever enters through SanitizeAndClamp, which strips every '\r', so the formatted text
        // splits on '\n' alone.
        string formatted = FormatEntry(entry);

        // A streaming flush appends to the text it wrapped last time. Every line the stream has already
        // terminated with '\n' is final, so its wrapped lines are reused and only the newly terminated
        // lines and the still-open tail are wrapped. Reuse is decided by comparing the settled prefix
        // itself, not by trusting that the entry only grew: the prefix label changes when streaming
        // starts and ends, and a sanitized lone surrogate can change when its pair arrives.
        string[] settledLines = [];
        int settledLength = 0;
        if (cached is not null
            && cached.WrapWidth == wrapWidth
            && cached.SettledLength > 0
            && cached.SettledLength <= formatted.Length
            && formatted.AsSpan(0, cached.SettledLength).SequenceEqual(
                cached.FormattedText.AsSpan(0, cached.SettledLength)))
        {
            settledLines = cached.SettledLines;
            settledLength = cached.SettledLength;
        }

        int newSettledLength = formatted.LastIndexOf('\n') + 1;
        if (newSettledLength > settledLength)
        {
            List<string> settled = new(settledLines);
            int start = settledLength;
            while (start < newSettledLength)
            {
                int end = formatted.IndexOf('\n', start);
                AddWrappedSegment(settled, formatted[start..end], wrapWidth);
                start = end + 1;
            }

            settledLines = settled.ToArray();
            settledLength = newSettledLength;
        }

        List<string> all = new(settledLines.Length + 1);
        all.AddRange(settledLines);
        AddWrappedSegment(all, formatted[settledLength..], wrapWidth);
        string[] lines = all.ToArray();

        _wrapCache[entry.Id] = new WrappedEntryLines(
            entry.Text,
            entry.Streaming,
            wrapWidth,
            lines,
            formatted,
            settledLength,
            settledLines);

        return lines;
    }

    private void AddWrappedSegment(List<string> lines, string segment, int wrapWidth)
    {
        WrappedSegmentCount++;
        if (wrapWidth > 1)
        {
            lines.AddRange(WrapLine(segment, wrapWidth));
        }
        else
        {
            lines.Add(segment);
        }
    }

    private void PruneWrapCacheUnlocked()
    {
        if (_wrapCache.Count <= _entries.Count)
        {
            return;
        }

        HashSet<Guid> live = new(_entries.Select(static e => e.Id));
        foreach (Guid id in _wrapCache.Keys.ToArray())
        {
            if (!live.Contains(id))
            {
                _ = _wrapCache.Remove(id);
            }
        }
    }

    /// <summary>Soft-wrap a single line to <paramref name="width"/> display cells (word-aware).</summary>
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

        // Fast path: printable ASCII that already fits needs no grapheme walk.
        if (line.Length <= width && TerminalCellMetrics.IsSimpleNarrow(line))
        {
            yield return line;
            yield break;
        }

        int index = 0;
        while (index < line.Length)
        {
            // Width is a terminal column budget, so the window ends where the cells run out — a CJK
            // ideograph or emoji consumes two of them even though it is one or two UTF-16 units.
            CellSlice window = TerminalCellMetrics.TakeCells(line, index, width);
            if (window.ReachedEnd)
            {
                yield return line[index..];
                yield break;
            }

            int sliceEnd = window.EndIndex;

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
                // Hard break for unbroken tokens longer than width, on a grapheme boundary.
                yield return line[index..sliceEnd];
                index = sliceEnd;
            }
        }
    }

    private static string FormatEntry(SessionLogEntry entry)
    {
        string prefix = entry.Kind switch
        {
            SessionLogEntryKind.User => "Dungeon Master: ",
            SessionLogEntryKind.Reasoning => "Reasoning (ephemeral): ",
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

    /// <summary>
    /// The one place text enters an entry. Control characters and escape sequences are stripped here —
    /// not when a line is formatted — because model and tool text is attacker-influenced and the
    /// transcript hands its lines to the terminal, and because stripping at the boundary leaves the
    /// wrap cache's reference check on <see cref="SessionLogEntry.Text"/> effective: clean text keeps
    /// its string instance. The cap is applied after the strip so a tab expansion cannot overshoot it.
    /// </summary>
    private string SanitizeAndClamp(SessionLogEntryKind kind, string text)
    {
        text = TerminalTextSanitizer.SanitizeBlock(text);

        int max = kind switch
        {
            SessionLogEntryKind.Command => MaxCommandChars,
            SessionLogEntryKind.Tool => MaxToolChars,
            SessionLogEntryKind.Reasoning => MaxReasoningChars,
            SessionLogEntryKind.Assistant => MaxAssistantChars,
            SessionLogEntryKind.User => MaxAssistantChars,
            _ => MaxCommandChars,
        };

        if (text.Length <= max)
        {
            return text;
        }

        string marker = kind == SessionLogEntryKind.Reasoning
            ? ReasoningTruncationMarker
            : TruncationMarker;
        int keep = Math.Max(0, max - marker.Length);

        // A character-count cutoff can land between the halves of a surrogate pair; the shared helper
        // nudges the boundary back so an astral-plane glyph is kept or dropped whole.
        return text[..Utf8Truncation.SafeCharSliceLength(text, keep)] + marker;
    }

    private void TrimUnlocked()
    {
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }

        PruneWrapCacheUnlocked();
    }

    /// <summary>
    /// Cached wrapped display lines for one entry at one wrap width. <paramref name="SettledLength"/> is
    /// how much of <paramref name="FormattedText"/> ends in a newline, and <paramref name="SettledLines"/>
    /// are the wrapped lines of exactly that prefix, kept so a growing entry re-wraps only its new tail.
    /// </summary>
    private sealed record WrappedEntryLines(
        string SourceText,
        bool Streaming,
        int WrapWidth,
        string[] Lines,
        string FormattedText,
        int SettledLength,
        string[] SettledLines);
}
