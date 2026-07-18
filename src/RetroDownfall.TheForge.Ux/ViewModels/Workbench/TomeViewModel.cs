using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.TheForge.Ux.Models;
using RetroDownfall.TheForge.Ux.Services;
using RetroDownfall.TheForge.Ux.ViewModels.FoundryFloor;

namespace RetroDownfall.TheForge.Ux.ViewModels.Workbench;

/// <summary>
/// Workbench document for a Session — The Tome. Streams standalone chat via NDJSON
/// <c>ping-stream</c>, observes live entries over session SSE, and supports fork / export /
/// manual entry. Owns a <see cref="CancellationTokenSource"/> cancelled on dispose (tab close).
/// </summary>
public sealed partial class TomeViewModel : ViewModelBase, IDisposable
{

    private readonly ITomeDataSource _dataSource;

    private readonly INavigationService _navigation;

    private readonly FoundryFloorViewModel _foundryFloor;

    private readonly IClipboardService _clipboard;

    private readonly CancellationTokenSource _lifetimeCts = new();

    private CancellationTokenSource? _sendCts;

    private ChatMessageViewModel? _streamingAssistant;

    private readonly Dictionary<string, ToolCallCardViewModel> _toolCardsByCallId = new(StringComparer.Ordinal);

    private bool _disposed;

    [ObservableProperty]
    private SessionDetailDto? _session;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private double _manaPercent;

    [ObservableProperty]
    private ChatCompletionUsage? _lastUsage;

    [ObservableProperty]
    private bool _wardPending;

    [ObservableProperty]
    private string? _pendingWardId;

    [ObservableProperty]
    private string? _lastWhisper;

    [ObservableProperty]
    private string? _manualEntryText;

    [ObservableProperty]
    private string? _lastExportContent;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _memoryManagementDisabled;

    [ObservableProperty]
    private string? _memoryStatusText;

    public TomeViewModel(
        Guid sessionId,
        ITomeDataSource dataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor,
        IClipboardService clipboard)
    {

        SessionId = sessionId;

        _dataSource = dataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        _clipboard = clipboard;

        Title = $"Tome: {sessionId:D}";

        Trace = new InferenceTraceViewModel();

    }

    public override DocumentKind? Kind => DocumentKind.Session;

    public Guid SessionId { get; private set; }

    public InferenceTraceViewModel Trace { get; }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public string MemoryManagementDisabledMessage =>
        DisabledSettingPaths.FormatEnableMessage("Session memory management", DisabledSettingPaths.SessionMemoryManagement);

    [RelayCommand]
    private async Task CopyDisabledPathsAsync(CancellationToken cancellationToken) =>
        await _clipboard
            .SetTextAsync(DisabledSettingPaths.JoinForClipboard(DisabledSettingPaths.SessionMemoryManagement), cancellationToken)
            .ConfigureAwait(true);

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken)
    {

        IsBusy = true;

        try
        {

            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);

            Session = await _dataSource.GetSessionAsync(SessionId, linked.Token).ConfigureAwait(true);

            if (Session is not null)
            {

                Title = string.IsNullOrWhiteSpace(Session.Title)
                    ? $"Tome: {Session.Id:D}"
                    : Session.Title!;

            }

            await RefreshEntriesAsync(linked.Token).ConfigureAwait(true);

            StartSessionObservation();

        }
        finally
        {

            IsBusy = false;

        }

    }

    [RelayCommand]
    public async Task SendAsync(CancellationToken cancellationToken)
    {

        string prompt = InputText.Trim();

        if (string.IsNullOrEmpty(prompt) || IsStreaming)
        {

            return;

        }

        InputText = string.Empty;

        Messages.Add(new ChatMessageViewModel("user", prompt));

        _streamingAssistant = null;

        _sendCts?.Cancel();

        _sendCts?.Dispose();

        _sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);

        CancellationToken sendToken = _sendCts.Token;

        IsStreaming = true;

        Trace.BeginCapture("session", SessionId.ToString("D"));

        try
        {

            PingRequest request = new(prompt, SessionId: SessionId);

            await foreach (IntelligenceEvent ev in _dataSource.PingStreamAsync(request, sendToken).ConfigureAwait(true))
            {

                ApplyIntelligenceEvent(ev);

            }

        }
        catch (OperationCanceledException) when (sendToken.IsCancellationRequested)
        {

            // Tab close or explicit cancel — leave partial transcript as-is.

        }
        catch (Exception ex)
        {

            AppendInlineError(ex.Message);

            _foundryFloor.AppendLine($"Tome stream error: {ex.Message}");

        }
        finally
        {

            IsStreaming = false;

            _streamingAssistant = null;

        }

    }

    [RelayCommand]
    public async Task AppendManualEntryAsync(CancellationToken cancellationToken)
    {

        string content = ManualEntryText?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(content))
        {

            return;

        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        EntryDto? entry = await _dataSource
            .AppendEntryAsync(SessionId, new AppendEntryRequest(MessageRole.System, content), linked.Token)
            .ConfigureAwait(true);

        ManualEntryText = string.Empty;

        if (entry is not null)
        {

            AppendEntryIfNew(entry);

        }

    }

    [RelayCommand]
    public async Task ForkAsync(CancellationToken cancellationToken)
    {

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        SessionDetailDto? forked = await _dataSource
            .ForkAsync(SessionId, new ForkSessionRequest(), linked.Token)
            .ConfigureAwait(true);

        if (forked is not null)
        {

            _navigation.OpenDocument(DocumentKind.Session, forked.Id.ToString("D"));

        }

    }

    [RelayCommand]
    public async Task ExportAsync(CancellationToken cancellationToken)
    {

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        SessionExportResult? export = await _dataSource
            .ExportAsync(SessionId, "markdown", linked.Token)
            .ConfigureAwait(true);

        LastExportContent = export?.Content;

    }

    [RelayCommand]
    public async Task RefreshEntriesAsync(CancellationToken cancellationToken)
    {

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        DataSourceResult<EntryDto[]> result = await _dataSource
            .GetEntriesAsync(SessionId, offset: null, limit: null, linked.Token)
            .ConfigureAwait(true);

        if (!result.Success)
        {

            MemoryStatusText = result.ErrorMessage ?? "Failed to load session entries.";

            _foundryFloor.AppendLine($"Tome entries refresh failed: {MemoryStatusText}");

            return;

        }

        Messages.Clear();

        _toolCardsByCallId.Clear();

        _streamingAssistant = null;

        if (result.Data is { } entries)
        {

            foreach (EntryDto entry in entries.OrderBy(static e => e.CreatedAt))
            {

                AppendEntryIfNew(entry);

            }

        }

        MemoryStatusText = $"{Messages.Count} entries.";

    }

    [RelayCommand]
    public async Task PinEntryAsync(ChatMessageViewModel? message, CancellationToken cancellationToken)
    {

        if (message?.EntryId is not { } entryId || MemoryManagementDisabled)
        {

            return;

        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        DataSourceResult<bool> result = await _dataSource
            .PinEntryAsync(SessionId, entryId, linked.Token)
            .ConfigureAwait(true);

        if (result.Success)
        {

            message.IsPinned = true;

            MemoryStatusText = "Entry pinned.";

            return;

        }

        if (result.ErrorCode == ErrorCodes.Session.MemoryManagementDisabled)
        {

            ApplyMemoryManagementDisabled(result.ErrorMessage);

            return;

        }

        if (result.ErrorCode == ErrorCodes.Session.TooManyPinned)
        {

            MemoryStatusText = result.ErrorMessage ?? "Too many pinned entries.";

            _foundryFloor.AppendLine($"Tome pin failed: {MemoryStatusText}");

            return;

        }

        MemoryStatusText = result.ErrorMessage ?? "Failed to pin entry.";

        _foundryFloor.AppendLine($"Tome pin failed: {MemoryStatusText}");

    }

    [RelayCommand]
    public async Task UnpinEntryAsync(ChatMessageViewModel? message, CancellationToken cancellationToken)
    {

        if (message?.EntryId is not { } entryId || MemoryManagementDisabled)
        {

            return;

        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        DataSourceResult<bool> result = await _dataSource
            .UnpinEntryAsync(SessionId, entryId, linked.Token)
            .ConfigureAwait(true);

        if (result.Success)
        {

            message.IsPinned = false;

            MemoryStatusText = "Entry unpinned.";

            return;

        }

        if (result.ErrorCode == ErrorCodes.Session.MemoryManagementDisabled)
        {

            ApplyMemoryManagementDisabled(result.ErrorMessage);

            return;

        }

        MemoryStatusText = result.ErrorMessage ?? "Failed to unpin entry.";

        _foundryFloor.AppendLine($"Tome unpin failed: {MemoryStatusText}");

    }

    [RelayCommand]
    public async Task DeleteEntryAsync(ChatMessageViewModel? message, CancellationToken cancellationToken)
    {

        if (message?.EntryId is not { } entryId || MemoryManagementDisabled)
        {

            return;

        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        DataSourceResult<bool> result = await _dataSource
            .DeleteEntryAsync(SessionId, entryId, linked.Token)
            .ConfigureAwait(true);

        if (result.Success)
        {

            Messages.Remove(message);

            MemoryStatusText = "Entry deleted.";

            return;

        }

        if (result.ErrorCode == ErrorCodes.Session.MemoryManagementDisabled)
        {

            ApplyMemoryManagementDisabled(result.ErrorMessage);

            return;

        }

        MemoryStatusText = result.ErrorMessage ?? "Failed to delete entry.";

        _foundryFloor.AppendLine($"Tome delete entry failed: {MemoryStatusText}");

    }

    [RelayCommand]
    public async Task CompactAsync(CancellationToken cancellationToken)
    {

        if (MemoryManagementDisabled)
        {

            return;

        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCts.Token);

        DataSourceResult<CompactResult> result = await _dataSource
            .CompactAsync(SessionId, linked.Token)
            .ConfigureAwait(true);

        if (result.Success && result.Data is { } compact)
        {

            MemoryStatusText =
                $"Compacted: {compact.EntriesRemoved} entries removed ({compact.TokensBefore} → {compact.TokensAfter} tokens).";

            await RefreshEntriesAsync(linked.Token).ConfigureAwait(true);

            return;

        }

        if (result.ErrorCode == ErrorCodes.Session.MemoryManagementDisabled)
        {

            ApplyMemoryManagementDisabled(result.ErrorMessage);

            return;

        }

        MemoryStatusText = result.ErrorMessage ?? "Failed to compact session.";

        _foundryFloor.AppendLine($"Tome compact failed: {MemoryStatusText}");

    }

    [RelayCommand]
    private void DivineSession() => _navigation.FocusPanel(PanelKind.Divination);

    public void Dispose()
    {

        if (_disposed)
        {

            return;

        }

        _disposed = true;

        _lifetimeCts.Cancel();

        _lifetimeCts.Dispose();

        _sendCts?.Cancel();

        _sendCts?.Dispose();

        GC.SuppressFinalize(this);

    }

    private void StartSessionObservation()
    {

        _ = ObserveSessionEntriesAsync(_lifetimeCts.Token);

    }

    private async Task ObserveSessionEntriesAsync(CancellationToken cancellationToken)
    {

        try
        {

            await foreach (EntryDto entry in _dataSource.StreamEntriesAsync(SessionId, since: null, cancellationToken)
                               .ConfigureAwait(true))
            {

                AppendEntryIfNew(entry);

            }

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            // Expected on tab close.

        }
        catch (Exception ex)
        {

            _foundryFloor.AppendLine($"Tome session SSE error: {ex.Message}");

        }

    }

    private void ApplyIntelligenceEvent(IntelligenceEvent ev)
    {

        Trace.Capture(ev);

        switch (ev.Type)
        {
            case IntelligenceEventType.Token:
                AppendToken(ev.Data ?? string.Empty);
                break;

            case IntelligenceEventType.ToolCall:
                ApplyToolCall(ev);
                break;

            case IntelligenceEventType.ToolResult:
                ApplyToolResult(ev);
                break;

            case IntelligenceEventType.ToolError:
                ApplyToolError(ev);
                break;

            case IntelligenceEventType.Warded:
                WardPending = true;
                PendingWardId = ev.WardId;
                LastWhisper = string.IsNullOrWhiteSpace(ev.Message)
                    ? $"Ward pending{(string.IsNullOrWhiteSpace(ev.WardToolName) ? string.Empty : $": {ev.WardToolName}")}"
                    : ev.Message;
                break;

            case IntelligenceEventType.WardResolved:
                WardPending = false;
                PendingWardId = null;
                LastWhisper = string.IsNullOrWhiteSpace(ev.Message) ? "Ward resolved." : ev.Message;
                break;

            case IntelligenceEventType.Status:
                Messages.Add(new ChatMessageViewModel("status", ev.Message));
                break;

            case IntelligenceEventType.SessionBound:
                if (Guid.TryParse(ev.Message, out Guid boundId))
                {
                    SessionId = boundId;
                    if (Session is null)
                    {
                        Session = new SessionDetailDto(
                            boundId,
                            null,
                            Title,
                            "active",
                            Messages.Count,
                            DateTimeOffset.UtcNow,
                            DateTimeOffset.UtcNow,
                            null,
                            0);
                    }
                    else if (Session.Id != boundId)
                    {
                        Session = Session with { Id = boundId };
                    }
                }
                break;

            case IntelligenceEventType.ConversationBound:
                // Deprecated alias of sessionBound — ignore.
                break;

            case IntelligenceEventType.Result:
                LastUsage = ev.Usage;
                if (ev.Usage is not null && ev.Usage.TotalTokens > 0)
                {
                    // Context-window utilization is unknown here; surface completion share as a
                    // provisional Mana bar until Phase 10 / Anvil supply a real window size.
                    ManaPercent = Math.Clamp(
                        100.0 * ev.Usage.CompletionTokens / Math.Max(1, ev.Usage.TotalTokens),
                        0,
                        100);
                }
                break;

            case IntelligenceEventType.Error:
                AppendInlineError(ev.Message);
                _foundryFloor.AppendLine($"Tome error: {ev.Message}");
                break;
        }

    }

    private void AppendToken(string data)
    {

        if (string.IsNullOrEmpty(data))
        {

            return;

        }

        if (_streamingAssistant is null)
        {

            _streamingAssistant = new ChatMessageViewModel("assistant", data);

            Messages.Add(_streamingAssistant);

            return;

        }

        _streamingAssistant.AppendContent(data);

    }

    private void ApplyToolCall(IntelligenceEvent ev)
    {

        string callId = ev.ToolCall?.CallId ?? ev.Message;

        string name = ev.ToolCall?.Name ?? ev.WardToolName ?? ev.Message;

        string argumentsJson = ev.ToolCall?.ArgumentsJson ?? ev.Data ?? string.Empty;

        if (string.IsNullOrWhiteSpace(callId))
        {

            callId = Guid.NewGuid().ToString("N");

        }

        if (_toolCardsByCallId.TryGetValue(callId, out ToolCallCardViewModel? existing))
        {

            existing.Name = name;

            existing.ArgumentsJson = argumentsJson;

            return;

        }

        ToolCallCardViewModel card = new(callId, name, argumentsJson);

        _toolCardsByCallId[callId] = card;

        Messages.Add(new ChatMessageViewModel("tool", name, card));

    }

    private void ApplyToolResult(IntelligenceEvent ev)
    {

        string callId = ev.ToolCall?.CallId ?? ev.Message;

        string result = ev.Data ?? ev.Message;

        if (!string.IsNullOrWhiteSpace(callId) && _toolCardsByCallId.TryGetValue(callId, out ToolCallCardViewModel? card))
        {

            card.Result = result;

            return;

        }

        Messages.Add(new ChatMessageViewModel("tool", result));

    }

    private void ApplyToolError(IntelligenceEvent ev)
    {

        string callId = ev.ToolCall?.CallId ?? ev.Message;

        string error = ev.Message;

        if (!string.IsNullOrWhiteSpace(callId) && _toolCardsByCallId.TryGetValue(callId, out ToolCallCardViewModel? card))
        {

            card.HasError = true;

            card.ErrorMessage = error;

            return;

        }

        Messages.Add(new ChatMessageViewModel("error", error));

    }

    private void AppendInlineError(string message)
    {

        Messages.Add(new ChatMessageViewModel("error", message));

    }

    private void AppendEntryIfNew(EntryDto entry)
    {

        if (Messages.Any(m => m.EntryId == entry.Id))
        {

            return;

        }

        // Conservative backfill: attach server identity only when exactly one transient bubble matches.
        ChatMessageViewModel[] transientMatches = Messages
            .Where(m =>
                m.EntryId is null
                && string.Equals(m.Role, entry.Role, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Content, entry.Content, StringComparison.Ordinal))
            .ToArray();

        if (transientMatches.Length == 1)
        {

            ChatMessageViewModel match = transientMatches[0];

            match.EntryId = entry.Id;

            match.IsPinned = entry.IsPinned;

            return;

        }

        if (transientMatches.Length > 1)
        {

            // Ambiguous — leave bubbles transient; RefreshEntriesAsync is the source of truth.
            return;

        }

        if (!string.IsNullOrWhiteSpace(entry.ToolCallId) || !string.IsNullOrWhiteSpace(entry.ToolName))
        {

            string callId = entry.ToolCallId ?? entry.Id.ToString("D");

            if (_toolCardsByCallId.ContainsKey(callId))
            {

                return;

            }

            ToolCallCardViewModel card = new(callId, entry.ToolName ?? "tool", argumentsJson: string.Empty)
            {
                Result = entry.Content,
            };

            _toolCardsByCallId[callId] = card;

            Messages.Add(new ChatMessageViewModel("tool", entry.ToolName ?? "tool", card, entry.Id, entry.IsPinned));

            return;

        }

        Messages.Add(new ChatMessageViewModel(entry.Role, entry.Content, entryId: entry.Id, isPinned: entry.IsPinned));

    }

    private void ApplyMemoryManagementDisabled(string? message)
    {

        MemoryManagementDisabled = true;

        MemoryStatusText = message
            ?? DisabledSettingPaths.FormatEnableMessage("Session memory management", DisabledSettingPaths.SessionMemoryManagement);

        _foundryFloor.AppendLine($"Tome: {MemoryStatusText}");

    }

}
