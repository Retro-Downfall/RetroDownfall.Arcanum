using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.TheForge.Core.Models.Traces;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Reusable inference-stream timeline. Captures only frames present on the NDJSON stream —
/// does not claim full provider request messages, assembled system prompts, or Sanctum internals
/// unless those were emitted as events.
/// </summary>
public sealed partial class InferenceTraceViewModel : ObservableObject
{

    public const string LimitationsText =
        "This trace shows stream events only (status, session binding, tokens, reasoning metadata with body redacted, tools, wards, result/usage, errors). "
        + "It does not include full provider request messages, full assembled system prompts for arbitrary Tome runs, "
        + "or Sanctum decision details unless those were emitted on the stream.";

    public const string SensitiveHistoryWarning =
        "This history may contain prompts, model outputs, tool arguments, and file snippets. It is stored locally on this machine.";

    private readonly IInferenceTraceStore? _store;

    private readonly IArtifactFileDialogService? _fileDialog;

    private readonly Action? _openSpellCastPreview;

    private readonly Action? _openPromptTestPreview;

    private string? _openToolRoundId;

    [ObservableProperty]
    private string? _sourceKind;

    [ObservableProperty]
    private string? _sourceId;

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private string? _lastError;

    public InferenceTraceViewModel(
        IInferenceTraceStore? store = null,
        IArtifactFileDialogService? fileDialog = null,
        Action? openSpellCastPreview = null,
        Action? openPromptTestPreview = null)
    {

        _store = store;

        _fileDialog = fileDialog;

        _openSpellCastPreview = openSpellCastPreview;

        _openPromptTestPreview = openPromptTestPreview;

    }

    public ObservableCollection<InferenceTraceEntryViewModel> Entries { get; } = [];

    public string LimitationsBanner => LimitationsText;

    public string SensitiveWarning => SensitiveHistoryWarning;

    public void BeginCapture(string sourceKind, string? sourceId)
    {

        Clear();

        SourceKind = sourceKind;

        SourceId = sourceId;

        StatusText = "Capturing…";

    }

    public void Capture(IntelligenceEvent ev)
    {

        ArgumentNullException.ThrowIfNull(ev);

        string? toolRoundId = null;

        string? toolCallId = ev.ToolCall?.CallId;

        string? toolName = ev.ToolCall?.Name ?? ev.WardToolName;

        if (ev.Type == IntelligenceEventType.ToolCall)
        {

            _openToolRoundId = string.IsNullOrWhiteSpace(toolCallId) ? Guid.NewGuid().ToString("N") : toolCallId;

            toolRoundId = _openToolRoundId;

        }
        else if (ev.Type is IntelligenceEventType.ToolResult or IntelligenceEventType.ToolError)
        {

            toolRoundId = _openToolRoundId;

            if (ev.Type == IntelligenceEventType.ToolResult)
            {

                _openToolRoundId = null;

            }

        }
        else if (ev.Type is IntelligenceEventType.Warded or IntelligenceEventType.WardResolved)
        {

            toolRoundId = _openToolRoundId;

        }

        if (ev.Type == IntelligenceEventType.SessionBound && Guid.TryParse(ev.Message, out Guid sessionId))
        {

            SessionId = sessionId.ToString("D");

        }

        bool redactReasoning = ev.Type == IntelligenceEventType.Reasoning;
        Entries.Add(new InferenceTraceEntryViewModel(
            ev.Type.ToString(),
            redactReasoning ? "[reasoning body redacted]" : ev.Message,
            redactReasoning ? null : ev.Data,
            ev.Usage?.PromptTokens,
            ev.Usage?.CompletionTokens,
            ev.Usage?.TotalTokens,
            ev.Usage?.CachedTokens,
            ev.FinishReason,
            toolCallId,
            toolName,
            toolRoundId,
            ev.Timestamp ?? DateTimeOffset.UtcNow,
            ev.Reasoning?.Output.ToString(),
            ev.Usage is null ? null : ev.Usage.ReasoningTokens));

    }

    [RelayCommand]
    public void Clear()
    {

        Entries.Clear();

        SessionId = null;

        LastError = null;

        StatusText = null;

        _openToolRoundId = null;

    }

    public string BuildExportJson()
    {

        InferenceTraceRecord record = ToRecord(Guid.NewGuid(), DateTimeOffset.UtcNow);

        return JsonSerializer.Serialize(record, TheForgeInferenceTracesJsonContext.Default.InferenceTraceRecord);

    }

    [RelayCommand]
    public async Task ExportAsync(CancellationToken cancellationToken)
    {

        if (_fileDialog is null)
        {

            LastError = "Export dialog unavailable.";

            return;

        }

        string? path = await _fileDialog
            .PickSaveJsonPathAsync($"inference-trace-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json", cancellationToken)
            .ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(path))
        {

            return;

        }

        await File.WriteAllTextAsync(path, BuildExportJson(), Encoding.UTF8, cancellationToken).ConfigureAwait(true);

        StatusText = "Trace exported.";

    }

    [RelayCommand]
    public async Task PersistAsync(CancellationToken cancellationToken)
    {

        if (_store is null)
        {

            LastError = "Local trace store unavailable.";

            return;

        }

        InferenceTraceStoreDocument document = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        List<InferenceTraceRecord> traces = [ToRecord(Guid.NewGuid(), DateTimeOffset.UtcNow), .. document.Traces];

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _store.SaveAsync(
                new InferenceTraceStoreDocument(InferenceTraceStore.CurrentSchemaVersion, document.CreatedAt, now, traces),
                cancellationToken)
            .ConfigureAwait(true);

        StatusText = "Trace saved locally.";

    }

    [RelayCommand]
    public void OpenSessionInTome(INavigationService? navigation)
    {

        if (navigation is null || string.IsNullOrWhiteSpace(SessionId))
        {

            return;

        }

        navigation.OpenDocument(DocumentKind.Session, SessionId);

    }

    [RelayCommand]
    private void OpenSpellCastPreview()
    {

        if (_openSpellCastPreview is null)
        {

            StatusText = "Open the Spell editor Cast tab for assembled-context preview (no general dry-run API).";

            return;

        }

        _openSpellCastPreview();

    }

    [RelayCommand]
    private void OpenPromptTestPreview()
    {

        if (_openPromptTestPreview is null)
        {

            StatusText = "Open The Scriptorium Test tab for assembled-context preview (no general dry-run API).";

            return;

        }

        _openPromptTestPreview();

    }

    public InferenceTraceRecord ToRecord(Guid id, DateTimeOffset capturedAt)
    {

        List<InferenceTraceEventRecord> events = Entries
            .Select(static e => new InferenceTraceEventRecord(
                e.Type,
                e.Message,
                e.Data,
                e.PromptTokens,
                e.CompletionTokens,
                e.TotalTokens,
                e.CachedTokens,
                e.FinishReason,
                e.ToolCallId,
                e.ToolName,
                e.ToolRoundId,
                e.Timestamp,
                e.ReasoningOutputMode,
                e.ReasoningTokens))
            .ToList();

        return new InferenceTraceRecord(
            id,
            capturedAt,
            SourceKind ?? "unknown",
            SourceId,
            SessionId,
            events);

    }

}

public sealed record InferenceTraceEntryViewModel(
    string Type,
    string Message,
    string? Data,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? CachedTokens,
    string? FinishReason,
    string? ToolCallId,
    string? ToolName,
    string? ToolRoundId,
    DateTimeOffset Timestamp,
    string? ReasoningOutputMode = null,
    int? ReasoningTokens = null)
{

    public string DisplayLine
    {

        get
        {

            StringBuilder sb = new();

            sb.Append(Type);

            if (!string.IsNullOrWhiteSpace(ToolRoundId))
            {

                sb.Append(" [round ").Append(ToolRoundId.AsSpan(0, Math.Min(8, ToolRoundId.Length))).Append(']');

            }

            if (!string.IsNullOrWhiteSpace(ToolName))
            {

                sb.Append(' ').Append(ToolName);

            }

            if (!string.IsNullOrWhiteSpace(Message))
            {

                sb.Append(": ").Append(Message);

            }

            if (!string.IsNullOrWhiteSpace(ReasoningOutputMode))
            {

                sb.Append(" mode=").Append(ReasoningOutputMode);

            }

            if (ReasoningTokens is int reasoningTokens)
            {

                sb.Append(" reasoningTokens=").Append(reasoningTokens);

            }

            if (TotalTokens is int total)
            {

                sb.Append(" (tokens=").Append(total);

                if (CachedTokens is int cached and > 0)
                {

                    sb.Append(" cached=").Append(cached);

                }

                sb.Append(')');

            }

            if (!string.IsNullOrWhiteSpace(FinishReason))
            {

                sb.Append(" finish=").Append(FinishReason);

            }

            return sb.ToString();

        }

    }

}
