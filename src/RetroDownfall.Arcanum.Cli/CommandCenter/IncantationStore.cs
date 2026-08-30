using System.Buffers;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

internal enum IncantationState
{
    Pending,
    Succeeded,
    Failed,
    Unknown,
}

/// <summary>One tool invocation keyed by <see cref="CallId"/>.</summary>
internal sealed class IncantationRecord
{
    /// <summary>
    /// Per-payload retention cap, mirroring <see cref="SessionLogBuffer.DefaultMaxToolChars"/>. A tool
    /// response may approach the host's tool-output cap (~1 MiB), and the store keeps up to
    /// <see cref="IncantationStore.DefaultMaxEntries"/> records for the whole session, so the raw text
    /// is bounded on the way in rather than carried for the life of the process.
    /// </summary>
    public const int MaxPayloadChars = 4_096;

    /// <summary>Longest scalar value kept when an oversized argument object is reduced.</summary>
    public const int MaxRetainedValueChars = 256;

    public const string TruncationMarker = "… [truncated]";

    public IncantationRecord(string callId, string toolName)
    {
        CallId = string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId.Trim();
        ToolName = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName.Trim();
        State = IncantationState.Pending;
        CreatedUtc = DateTimeOffset.UtcNow;
        UpdatedUtc = CreatedUtc;
    }

    public string CallId { get; }

    public string ToolName { get; private set; }

    public string? ArgumentsJson { get; private set; }

    public string? ResultText { get; private set; }

    public string? ErrorText { get; private set; }

    public IncantationState State { get; private set; }

    public DateTimeOffset CreatedUtc { get; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    /// <summary>When resume cannot parse structured fields — formatter shows only this.</summary>
    public string? SafeSummaryOverride { get; private set; }

    private readonly List<string> _wardNotes = new();

    /// <summary>Ward audit notes for this invocation (Incantations-only).</summary>
    public IReadOnlyList<string> WardNotes => _wardNotes;

    private long _revision;

    private IReadOnlyList<string>? _cachedLines;

    private int _cachedWidth = -1;

    private long _cachedRevision = -1;

    /// <summary>
    /// Formatted display lines for this record, memoized per wrap width until the record changes.
    /// The pane is re-copied on every layout pass — which the composer fires on every keystroke — and
    /// formatting re-parses the retained argument and result JSON, so an unchanged record must cost
    /// nothing on the second pass (mirrors <see cref="SessionLogBuffer"/>'s per-entry wrap cache).
    /// </summary>
    public IReadOnlyList<string> DisplayLines(int contentWidth)
    {
        // Read the revision first: a mutation racing the format below leaves the cache stamped with
        // the older revision, so the next caller recomputes rather than serving stale lines.
        long revision = Interlocked.Read(ref _revision);
        if (_cachedLines is { } cached && _cachedWidth == contentWidth && _cachedRevision == revision)
        {
            return cached;
        }

        IReadOnlyList<string> formatted = IncantationFormatter.FormatBlock(this, contentWidth);
        _cachedLines = formatted;
        _cachedWidth = contentWidth;
        _cachedRevision = revision;
        return formatted;
    }

    public void ApplyCall(string? toolName, string? argumentsJson)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            ToolName = toolName.Trim();
        }

        if (argumentsJson is not null)
        {
            ArgumentsJson = ClampArguments(argumentsJson);
        }

        if (State == IncantationState.Unknown)
        {
            State = IncantationState.Pending;
        }

        Touch();
    }

    public void ApplyResult(string? resultText)
    {
        // ToolError is emitted before ToolResult for tolerated failures — do not promote Failed → Succeeded.
        if (State == IncantationState.Failed)
        {
            if (!string.IsNullOrWhiteSpace(resultText) && string.IsNullOrWhiteSpace(ErrorText))
            {
                ErrorText = ClampPayload(resultText);
            }

            Touch();
            return;
        }

        ResultText = ClampPayload(resultText);
        ErrorText = null;
        State = IncantationState.Succeeded;
        Touch();
    }

    public void ApplyError(string? errorText)
    {
        ErrorText = ClampPayload(errorText);
        State = IncantationState.Failed;
        Touch();
    }

    public void AppendWardNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        string cleaned = note.Trim();
        if (_wardNotes.Count > 0
            && string.Equals(_wardNotes[^1], cleaned, StringComparison.Ordinal))
        {
            return;
        }

        _wardNotes.Add(cleaned);
        Touch();
    }

    public void MarkUnparseable(string? toolNameHint = null)
    {
        ToolName = string.IsNullOrWhiteSpace(toolNameHint) ? "unknown" : toolNameHint.Trim();
        State = IncantationState.Unknown;
        SafeSummaryOverride = "Tool interaction (details unavailable)";
        ArgumentsJson = null;
        ResultText = null;
        ErrorText = null;
        Touch();
    }

    /// <summary>Hard-caps a retained payload on a surrogate-safe boundary.</summary>
    private static string? ClampPayload(string? text)
    {
        if (text is null || text.Length <= MaxPayloadChars)
        {
            return text;
        }

        int keep = Math.Max(0, MaxPayloadChars - TruncationMarker.Length);
        return text[..Utf8Truncation.SafeCharSliceLength(text, keep)] + TruncationMarker;
    }

    /// <summary>
    /// Caps retained arguments. Cutting a JSON object mid-token would cost the formatter the safe
    /// summary it builds from the argument keys, so an oversized object is first reduced — every key
    /// survives (key names drive sensitive/heavy detection) while oversized values and array bodies
    /// are dropped. Anything that will not reduce falls back to a plain clamp.
    /// </summary>
    private static string? ClampArguments(string? argumentsJson)
    {
        if (argumentsJson is null || argumentsJson.Length <= MaxPayloadChars)
        {
            return argumentsJson;
        }

        return TryReduceArgumentObject(argumentsJson) ?? ClampPayload(argumentsJson);
    }

    private static string? TryReduceArgumentObject(string argumentsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            ArrayBufferWriter<byte> buffer = new();
            using (Utf8JsonWriter writer = new(buffer))
            {
                WriteReducedObject(document.RootElement, writer);
            }

            string reduced = Encoding.UTF8.GetString(buffer.WrittenSpan);
            return reduced.Length <= MaxPayloadChars ? reduced : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteReducedObject(JsonElement element, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WritePropertyName(property.Name);
                    WriteReducedObject(property.Value, writer);
                    break;
                case JsonValueKind.Array:
                    // Arrays feed neither the safe summary nor sensitive-key detection.
                    writer.WriteStartArray(property.Name);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    string value = property.Value.GetString() ?? string.Empty;
                    writer.WriteString(
                        property.Name,
                        value.Length <= MaxRetainedValueChars ? value : string.Empty);
                    break;
                default:
                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                    break;
            }
        }

        writer.WriteEndObject();
    }

    private void Touch()
    {
        UpdatedUtc = DateTimeOffset.UtcNow;
        _ = Interlocked.Increment(ref _revision);
    }
}

/// <summary>CallId-keyed store for the Incantations pane.</summary>
internal sealed class IncantationStore
{
    public const int DefaultMaxEntries = 500;

    private readonly object _gate = new();

    private readonly List<IncantationRecord> _order = new();

    private readonly Dictionary<string, IncantationRecord> _byCallId =
        new(StringComparer.Ordinal);

    public IncantationStore(int maxEntries = DefaultMaxEntries)
    {
        MaxEntries = Math.Max(1, maxEntries);
    }

    public int MaxEntries { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _byCallId.Clear();
        }
    }

    public IncantationRecord UpsertCall(string? callId, string? toolName, string? argumentsJson)
    {
        lock (_gate)
        {
            string id = NormalizeCallId(callId);
            if (_byCallId.TryGetValue(id, out IncantationRecord? existing))
            {
                existing.ApplyCall(toolName, argumentsJson);
                return existing;
            }

            IncantationRecord created = new(id, toolName ?? "unknown");
            created.ApplyCall(toolName, argumentsJson);
            _byCallId[created.CallId] = created;
            _order.Add(created);
            TrimUnlocked();
            return created;
        }
    }

    public IncantationRecord UpsertResult(string? callId, string? toolName, string? resultText)
    {
        lock (_gate)
        {
            IncantationRecord record = GetOrCreateUnlocked(callId, toolName);
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                record.ApplyCall(toolName, argumentsJson: null);
            }

            record.ApplyResult(resultText);
            return record;
        }
    }

    public IncantationRecord UpsertError(string? callId, string? toolName, string? errorText)
    {
        lock (_gate)
        {
            IncantationRecord record = GetOrCreateUnlocked(callId, toolName);
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                record.ApplyCall(toolName, argumentsJson: null);
            }

            record.ApplyError(errorText);
            return record;
        }
    }

    /// <summary>
    /// Completes the most recent pending invocation (optionally matching <paramref name="toolName"/>).
    /// Used when resumed <c>[ToolResult]</c> rows lack a CallId.
    /// </summary>
    public IncantationRecord CompleteLatestPending(string? toolName, string? resultOrError, bool isError)
    {
        lock (_gate)
        {
            IncantationRecord? match = null;
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                IncantationRecord candidate = _order[i];
                if (candidate.State != IncantationState.Pending)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(toolName)
                    && !string.Equals(candidate.ToolName, toolName.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                match = candidate;
                break;
            }

            match ??= GetOrCreateUnlocked(callId: null, toolName);
            if (isError)
            {
                match.ApplyError(resultOrError);
            }
            else
            {
                match.ApplyResult(resultOrError);
            }

            return match;
        }
    }

    /// <summary>
    /// Attach a ward status note to the matching tool invocation (latest pending preferred).
    /// Creates a pending placeholder when no CallId match exists yet.
    /// </summary>
    public IncantationRecord AppendWardNote(string? toolName, string note, string? wardId = null)
    {
        lock (_gate)
        {
            string name = string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName.Trim();
            IncantationRecord? match = null;
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                IncantationRecord candidate = _order[i];
                if (!string.Equals(candidate.ToolName, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (candidate.State == IncantationState.Pending)
                {
                    match = candidate;
                    break;
                }

                match ??= candidate;
            }

            if (match is null)
            {
                string id = string.IsNullOrWhiteSpace(wardId)
                    ? Guid.NewGuid().ToString("N")
                    : "ward:" + wardId.Trim();
                match = GetOrCreateUnlocked(id, name);
            }

            match.AppendWardNote(note);
            return match;
        }
    }

    /// <summary>Resume helper: create from Grimoire entry; unparseable → safe override.</summary>
    public IncantationRecord AddFromHistory(
        string? callId,
        string? toolName,
        string? argumentsJson,
        string? resultOrContent,
        bool isError,
        bool unparseable)
    {
        lock (_gate)
        {
            if (unparseable)
            {
                IncantationRecord bad = new(NormalizeCallId(callId), toolName ?? "unknown");
                bad.MarkUnparseable(toolName);
                _byCallId[bad.CallId] = bad;
                _order.Add(bad);
                TrimUnlocked();
                return bad;
            }

            IncantationRecord record = GetOrCreateUnlocked(callId, toolName);
            record.ApplyCall(toolName, argumentsJson);
            if (isError)
            {
                record.ApplyError(resultOrContent);
            }
            else if (resultOrContent is not null)
            {
                record.ApplyResult(resultOrContent);
            }

            return record;
        }
    }

    public IReadOnlyList<IncantationRecord> Snapshot()
    {
        lock (_gate)
        {
            return _order.ToArray();
        }
    }

    /// <summary>
    /// Formats display lines and returns the CallId for the first content line of each block
    /// (separator lines map to null).
    /// </summary>
    public void CopyDisplayLinesTo(
        ObservableCollection<string> target,
        List<string?> lineAnchors,
        int contentWidth)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(lineAnchors);

        List<string> lines = new();
        List<string?> anchors = new();

        // Format inside the gate, exactly as SessionLogBuffer.CopyLinesTo does. The chat thread mutates
        // these records for the whole of a streamed turn, and the formatter re-reads each nullable
        // payload after its own null check and indexes WardNotes after its own count — reading them
        // outside the lock throws on the Terminal.Gui main loop. The per-record memo keeps the repeat
        // cost of holding the gate here negligible.
        lock (_gate)
        {
            for (int i = 0; i < _order.Count; i++)
            {
                if (i > 0)
                {
                    string rule = IncantationFormatter.SeparatorLine(contentWidth);
                    lines.Add(rule);
                    anchors.Add(null);
                }

                IncantationRecord record = _order[i];
                IReadOnlyList<string> block = record.DisplayLines(contentWidth);
                foreach (string line in block)
                {
                    lines.Add(line);
                    anchors.Add(record.CallId);
                }
            }
        }

        // Tail edit rather than Clear-then-re-Add: the bound list view rescans the whole collection on
        // every change notification, so a full rebuild per streaming flush is quadratic in pane size.
        _ = CommandCenterLineSync.ApplyTailEdit(target, lines);

        lineAnchors.Clear();
        lineAnchors.AddRange(anchors);
    }

    private IncantationRecord GetOrCreateUnlocked(string? callId, string? toolName)
    {
        string id = NormalizeCallId(callId);
        if (_byCallId.TryGetValue(id, out IncantationRecord? existing))
        {
            return existing;
        }

        IncantationRecord created = new(id, toolName ?? "unknown");
        _byCallId[created.CallId] = created;
        _order.Add(created);
        TrimUnlocked();
        return created;
    }

    private void TrimUnlocked()
    {
        while (_order.Count > MaxEntries)
        {
            IncantationRecord oldest = _order[0];
            _order.RemoveAt(0);
            _ = _byCallId.Remove(oldest.CallId);
        }
    }

    private static string NormalizeCallId(string? callId) =>
        string.IsNullOrWhiteSpace(callId) ? Guid.NewGuid().ToString("N") : callId.Trim();
}
