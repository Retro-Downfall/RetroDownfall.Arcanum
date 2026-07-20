using System.Collections.ObjectModel;
using System.Text.Json;

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

    public void ApplyCall(string? toolName, string? argumentsJson)
    {
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            ToolName = toolName.Trim();
        }

        if (argumentsJson is not null)
        {
            ArgumentsJson = argumentsJson;
        }

        if (State == IncantationState.Unknown)
        {
            State = IncantationState.Pending;
        }

        Touch();
    }

    public void ApplyResult(string? resultText)
    {
        ResultText = resultText;
        ErrorText = null;
        State = IncantationState.Succeeded;
        Touch();
    }

    public void ApplyError(string? errorText)
    {
        ErrorText = errorText;
        State = IncantationState.Failed;
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

    private void Touch() => UpdatedUtc = DateTimeOffset.UtcNow;
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

        IncantationRecord[] snapshot = Snapshot().ToArray();
        target.Clear();
        lineAnchors.Clear();

        for (int i = 0; i < snapshot.Length; i++)
        {
            if (i > 0)
            {
                string rule = IncantationFormatter.SeparatorLine(contentWidth);
                target.Add(rule);
                lineAnchors.Add(null);
            }

            IReadOnlyList<string> block = IncantationFormatter.FormatBlock(snapshot[i], contentWidth);
            bool first = true;
            foreach (string line in block)
            {
                target.Add(line);
                lineAnchors.Add(first ? snapshot[i].CallId : snapshot[i].CallId);
                first = false;
            }
        }
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
