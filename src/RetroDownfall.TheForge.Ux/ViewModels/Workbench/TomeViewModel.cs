using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
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

    public TomeViewModel(
        Guid sessionId,
        ITomeDataSource dataSource,
        INavigationService navigation,
        FoundryFloorViewModel foundryFloor)
    {

        SessionId = sessionId;

        _dataSource = dataSource;

        _navigation = navigation;

        _foundryFloor = foundryFloor;

        Title = $"Tome: {sessionId:D}";

    }

    public override DocumentKind? Kind => DocumentKind.Session;

    public Guid SessionId { get; private set; }

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

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

        // Avoid duplicating the user bubble we already added locally for the in-flight Send.
        bool alreadyPresent = Messages.Any(m =>
            string.Equals(m.Role, entry.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(m.Content, entry.Content, StringComparison.Ordinal));

        if (alreadyPresent)
        {

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

            Messages.Add(new ChatMessageViewModel("tool", entry.ToolName ?? "tool", card));

            return;

        }

        Messages.Add(new ChatMessageViewModel(entry.Role, entry.Content));

    }

}
