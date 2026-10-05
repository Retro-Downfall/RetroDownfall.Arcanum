using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Mutable per-run Command Center state. Not a DI singleton.
/// </summary>
internal sealed class CommandCenterState
{
    public const int SessionPageSize = 40;

    public const int TranscriptPageSize = 200;

    public CommandCenterState(SessionLogBuffer log)
    {
        Log = log ?? throw new ArgumentNullException(nameof(log));
        Incantations = new IncantationStore();
    }

    public SessionLogBuffer Log { get; }

    public IncantationStore Incantations { get; }

    /// <summary>Synthetic Thinking spinner; not a <see cref="SessionLogEntry"/>.</summary>
    public bool ThinkingActive { get; set; }

    public int ThinkingTick { get; set; }

    public string ThinkingDisplay => ThinkingSpinner.Format(ThinkingTick);

    public Guid? SessionId { get; set; }

    public string? SessionTitle { get; set; }

    public string? SessionStatus { get; set; }

    public int? SessionEntryCount { get; set; }

    public Guid? ForkedFromSessionId { get; set; }

    public string? Model { get; set; }

    public bool ShowTelemetryPane { get; set; } = true;

    public Guid? CampaignId { get; set; }

    public ServeLaunchResult? ServeLaunch { get; set; }

    public string? HealthSummary { get; set; }

    public IReadOnlyList<McpServerInfo> McpServers { get; set; } = [];

    public long? ManaUsed { get; set; }

    public int? ManaLimit { get; set; }

    public ContextTokenBreakdown? LastContextBreakdown { get; set; }

    /// <summary>
    /// Guards both staged sets. The composer thread stages a path or reference while a turn on a worker
    /// thread snapshots and then clears what it sent, so neither set is ever exposed for mutation.
    /// </summary>
    private readonly object _stagedGate = new();

    private readonly HashSet<string> _stagedAttachmentPaths = new(StringComparer.Ordinal);

    private readonly HashSet<Guid> _stagedAttachmentReferences = [];

    /// <summary>Paths staged for the next turn; a point-in-time copy, so it is safe to enumerate.</summary>
    public IReadOnlyCollection<string> StagedAttachmentPaths
    {
        get
        {
            lock (_stagedGate)
            {
                return _stagedAttachmentPaths.ToArray();
            }
        }
    }

    /// <summary>
    /// Bound session attachment ids staged via <c>/attachments add</c> for the next turn
    /// (<see cref="PingRequest.AttachmentReferences"/>); a point-in-time copy.
    /// </summary>
    public IReadOnlyCollection<Guid> StagedAttachmentReferences
    {
        get
        {
            lock (_stagedGate)
            {
                return _stagedAttachmentReferences.ToArray();
            }
        }
    }

    /// <summary>Stages a file path for the next turn. Returns <see langword="false"/> if it was already staged.</summary>
    public bool StageAttachmentPath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        lock (_stagedGate)
        {
            return _stagedAttachmentPaths.Add(fullPath);
        }
    }

    /// <summary>Stages a session attachment reference for the next turn.</summary>
    public bool StageAttachmentReference(Guid attachmentId)
    {
        lock (_stagedGate)
        {
            return _stagedAttachmentReferences.Add(attachmentId);
        }
    }

    /// <summary>
    /// Takes both staged sets in one critical section, so a turn sees a consistent pair even while the
    /// composer thread stages more.
    /// </summary>
    public StagedAttachmentSnapshot SnapshotStaged()
    {
        lock (_stagedGate)
        {
            return new StagedAttachmentSnapshot(
                _stagedAttachmentPaths.ToArray(),
                _stagedAttachmentReferences.ToArray());
        }
    }

    /// <summary>
    /// Removes what a finished turn sent. Only the snapshot's own entries go, so anything staged while
    /// the turn ran stays staged for the next one.
    /// </summary>
    public void ClearStaged(StagedAttachmentSnapshot sent)
    {
        ArgumentNullException.ThrowIfNull(sent);

        lock (_stagedGate)
        {
            foreach (string path in sent.Paths)
            {
                _ = _stagedAttachmentPaths.Remove(path);
            }

            foreach (Guid id in sent.References)
            {
                _ = _stagedAttachmentReferences.Remove(id);
            }
        }
    }

    public IReadOnlyList<SessionAttachmentDto> SessionAttachments { get; set; } = [];

    private int _turnActive;

    /// <summary>True while a chat turn is in flight (atomic with <see cref="TryBeginTurn"/>).</summary>
    public bool Generating => Volatile.Read(ref _turnActive) != 0;

    /// <summary>
    /// Atomically claims the turn slot. Returns <see langword="false"/> if a turn is already active.
    /// </summary>
    public bool TryBeginTurn() => Interlocked.CompareExchange(ref _turnActive, 1, 0) == 0;

    /// <summary>Releases the turn slot claimed by <see cref="TryBeginTurn"/>.</summary>
    public void EndTurn() => Interlocked.Exchange(ref _turnActive, 0);

    public string StreamingAssistantText { get; set; } = string.Empty;

    private CancellationTokenSource? _turnCts;

    /// <summary>
    /// The current turn's token source. The submit path owns it — it creates, publishes, and disposes
    /// it — so everything else reads it through <see cref="TryCancelTurn"/> and <see cref="TurnTokenOr"/>,
    /// which tolerate the disposal that can land at any moment.
    /// </summary>
    public CancellationTokenSource? TurnCts
    {
        get => Volatile.Read(ref _turnCts);
        set => Volatile.Write(ref _turnCts, value);
    }

    /// <summary>
    /// Cancels the turn in flight. Returns <see langword="false"/> when there is none, or when the submit
    /// path disposed its source between the read and the call: Ctrl+C reaches this from a fire-and-forget
    /// task, where the <see cref="ObjectDisposedException"/> a disposed source throws would go unobserved.
    /// </summary>
    public bool TryCancelTurn()
    {
        CancellationTokenSource? turn = TurnCts;
        if (turn is null)
        {
            return false;
        }

        try
        {
            turn.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The turn finished and the submit path disposed its source first; nothing is left to cancel.
            return false;
        }
    }

    /// <summary>
    /// The current turn's token, or <paramref name="fallback"/> when no turn is in flight or its source
    /// has just been disposed (reading <see cref="CancellationTokenSource.Token"/> then throws).
    /// </summary>
    public CancellationToken TurnTokenOr(CancellationToken fallback)
    {
        CancellationTokenSource? turn = TurnCts;
        if (turn is null)
        {
            return fallback;
        }

        try
        {
            return turn.Token;
        }
        catch (ObjectDisposedException)
        {
            // Same race as TryCancelTurn: the source went away between the read and the call.
            return fallback;
        }
    }

    public bool RequestExit { get; set; }

    public int ExitCode { get; set; }

    public bool MonochromeTheme { get; set; }

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    public IReadOnlyList<SessionListItem> Sessions { get; set; } = [];

    public DateTimeOffset? SessionPageBeforeUpdatedAt { get; set; }

    public DateTimeOffset? NextSessionPageBeforeUpdatedAt { get; set; }

    public bool HasOlderSessionPage => NextSessionPageBeforeUpdatedAt is not null;

    public bool HasNewerSessionPage => SessionPageHistory.Count > 0;

    internal List<DateTimeOffset?> SessionPageHistory { get; } = [];

    public string SessionFilter { get; set; } = string.Empty;

    /// <summary>
    /// Models offered by the drop-down, fetched from <c>GET /api/models</c> — which is where the
    /// operator's hide list is already applied, so nothing hidden reaches this list.
    /// </summary>
    public IReadOnlyList<ModelPickerItem> ModelChoices { get; set; } = [];

    /// <summary>Type-ahead text narrowing <see cref="ModelChoices"/>.</summary>
    public string ModelFilter { get; set; } = string.Empty;

    /// <summary>Row the model drop-down has highlighted, within <see cref="FilteredModels"/>.</summary>
    public int SelectedModelIndex { get; set; }

    public IReadOnlyList<ModelPickerItem> FilteredModels =>
        CommandCenterModelPicker.Filter(ModelChoices, ModelFilter);

    public Guid? SelectedSessionId { get; set; }

    public Guid? SelectedTranscriptEntryId { get; set; }

    public IReadOnlyList<EntryDto> LoadedTranscriptEntries { get; set; } = [];

    public int TranscriptEntryOffset { get; set; }

    public bool HasOlderTranscriptEntries =>
        SessionEntryCount is { } total
        && TranscriptEntryOffset + LoadedTranscriptEntries.Count < total;

    public bool HasNewerTranscriptEntries => TranscriptEntryOffset > 0;

    public string? PendingAlternativePrompt { get; set; }

    public CommandCenterFocusRegion FocusRegion { get; set; } = CommandCenterFocusRegion.Composer;

    public CommandCenterOverlayKind Overlay { get; set; } = CommandCenterOverlayKind.None;

    /// <summary>Transient header/footer status (loading/refresh). Not written to the transcript.</summary>
    public string? TransientStatus { get; set; }

    /// <summary>Ephemeral footer hint (e.g. Press Ctrl+Q to quit).</summary>
    public string? FooterHint { get; set; }

    public string? LastError { get; set; }

    public bool IsStreaming => Generating;

    public IReadOnlyList<SessionListItem> FilteredSessions
    {
        get
        {
            string filter = SessionFilter.Trim();
            if (filter.Length == 0)
            {
                return Sessions;
            }

            return Sessions
                .Where(s =>
                    s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || s.ShortId.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || s.Id.ToString("D").Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || s.Id.ToString("N").Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
    }

    public string HeaderText
    {
        get
        {
            string health = ServeLaunch?.Status switch
            {
                ServeLaunchStatus.AlreadyRunning or ServeLaunchStatus.Started => "●",
                ServeLaunchStatus.Failed or ServeLaunchStatus.AuthFailed => "○",
                _ => "·",
            };

            string model = string.IsNullOrWhiteSpace(Model) ? "(default)" : Model!;
            string sessionLine = FormatSessionHeader();
            string generating = ThinkingActive
                ? $" · {ThinkingDisplay} Ctrl+C cancel"
                : Generating
                    ? " · Generating… Ctrl+C cancel"
                    : string.Empty;
            string transient = string.IsNullOrWhiteSpace(TransientStatus)
                ? string.Empty
                : $" · {TransientStatus}";

            return $"{health}  model={model}  {sessionLine}{generating}{transient}";
        }
    }

    public string FooterHints
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FooterHint))
            {
                return FooterHint!;
            }

            string controls = FocusRegion switch
            {
                CommandCenterFocusRegion.Sessions or CommandCenterFocusRegion.Overlay
                    when Overlay is CommandCenterOverlayKind.SessionPicker or CommandCenterOverlayKind.None
                    => "↑↓/jk select · Enter resume · Ctrl+PgDn/PgUp older/newer page · type to filter · Esc composer · Ctrl+R refresh · F1 help",
                CommandCenterFocusRegion.Transcript
                    => "↑↓ scroll · PgUp/PgDn · Ctrl+PgUp/PgDn load adjacent page · Home/End · Esc composer · Tab focus · F1 help",
                CommandCenterFocusRegion.Incantations
                    => "↑↓ scroll Incantations · PgUp/PgDn · Home/End · Esc composer · Tab focus · F1 help",
                CommandCenterFocusRegion.Model
                    => "Enter/Space open models · Esc composer · Tab focus · /model <name> also works",
                CommandCenterFocusRegion.Overlay when Overlay == CommandCenterOverlayKind.ModelPicker
                    => "↑↓/jk select · Enter use model · type to filter · Esc cancel",
                _
                    => "Ctrl+Enter send · Enter newline · Ctrl+K commands · Ctrl+O sessions · Ctrl+N new · Ctrl+R refresh · Ctrl+C cancel · Ctrl+Q quit · F1 · Tab",
            };

            string? indexing = AttachmentIndexingStatusText;

            return indexing is null ? controls : $"{indexing} | {controls}";
        }
    }

    public string? AttachmentIndexingStatusText
    {
        get
        {
            int pending = 0;

            int failed = 0;

            int indexed = 0;

            foreach (SessionAttachmentDto attachment in SessionAttachments)
            {
                switch (attachment.IndexingStatus)
                {
                    case SessionAttachmentIndexStatus.Pending:
                        pending++;
                        break;

                    case SessionAttachmentIndexStatus.Failed:
                        failed++;
                        break;

                    case SessionAttachmentIndexStatus.Indexed:
                        indexed++;
                        break;
                }
            }

            if (pending > 0)
            {
                string failure = failed > 0 ? $" · {failed:N0} failed" : string.Empty;

                return $"Indexing attachments: {pending:N0} pending{failure}";
            }

            if (failed > 0)
            {
                return $"Attachment indexing failed: {failed:N0}";
            }

            return indexed > 0
                ? $"Attachment indexing complete: {indexed:N0} indexed"
                : null;
        }
    }

    /// <summary>Legacy right-pane text retained for tests / compact fallbacks.</summary>
    public string SidebarText
    {
        get
        {
            int mcpUp = McpServers.Count(static s => s.State == McpServerState.Running);

            string mana = LastContextBreakdown is { } context
                ? $"Mana: {context.InputTokens}+{context.ReservedTokens} ({context.OverallClassification})"
                : ManaLimit is > 0
                    ? $"Mana: {ManaUsed ?? 0}/{ManaLimit}"
                    : ManaUsed is { } used
                        ? $"Mana: {used}"
                        : "Mana: -";

            string serve = ServeLaunch?.Status.ToString() ?? "Unknown";

            return string.Join(
                Environment.NewLine,
                [
                    "MCP",
                    $"  {mcpUp}/{McpServers.Count} up",
                    "",
                    mana,
                    "",
                    "Serve",
                    $"  {serve}",
                ]);
        }
    }

    public void ClearSessionBinding()
    {
        SessionId = null;
        SessionTitle = null;
        SessionStatus = null;
        SessionEntryCount = null;
        ForkedFromSessionId = null;
        SelectedSessionId = null;
        LastContextBreakdown = null;

        LoadedTranscriptEntries = [];

        TranscriptEntryOffset = 0;

        SessionAttachments = [];
    }

    public void ApplySessionMeta(
        Guid id,
        string? title,
        string? status,
        int? entryCount,
        Guid? forkedFromSessionId = null)
    {
        SessionId = id;
        SelectedSessionId = id;
        // Host text, stored from a conversation: stripped here so no pane has to remember to.
        string safeTitle = TerminalTextSanitizer.SanitizeLine(title).Trim();
        string safeStatus = TerminalTextSanitizer.SanitizeLine(status).Trim();
        SessionTitle = safeTitle.Length == 0 ? "Untitled" : safeTitle;
        SessionStatus = safeStatus.Length == 0 ? "Active" : safeStatus;
        SessionEntryCount = entryCount;
        ForkedFromSessionId = forkedFromSessionId;
    }

    private string FormatSessionHeader()
    {
        if (SessionId is not { } sid)
        {
            return "Session: New Session · first message will create it";
        }

        string title = string.IsNullOrWhiteSpace(SessionTitle) ? "Untitled" : SessionTitle!;
        string shortId = sid.ToString("N")[..8];
        string status = string.IsNullOrWhiteSpace(SessionStatus) ? "Active" : SessionStatus!;
        string branch = ForkedFromSessionId is null ? string.Empty : " · ⑂ branch";
        return $"Session: {title} · {shortId} · {status}{branch}";
    }
}

/// <summary>The attachments staged for the next turn, as one consistent copy.</summary>
internal sealed record StagedAttachmentSnapshot(string[] Paths, Guid[] References);

internal sealed record SessionListItem(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset UpdatedAt,
    int EntryCount,
    Guid? ForkedFromSessionId = null)
{
    public string ShortId => Id.ToString("N")[..8];

    public string DisplayLine
    {
        get
        {
            string title = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;

            // A code-unit slice can land between the halves of a surrogate pair; the shared metrics
            // walk graphemes and measure display cells, as every other pane does (DESIGN §16.6).
            if (TerminalCellMetrics.MeasureWidth(title) > 22)
            {
                title = TerminalCellMetrics.TruncateToCells(title, 21) + "…";
            }

            string branch = ForkedFromSessionId is null ? string.Empty : " ⑂";
            return $"{title}  {ShortId}{branch}";
        }
    }

    /// <summary>
    /// A row for a host Session summary. The title and status are text the host stored from a
    /// conversation, so they are stripped of anything a terminal would act on before they can reach the
    /// sidebar or the session picker.
    /// </summary>
    public static SessionListItem FromSummary(SessionSummaryDto dto)
    {
        string title = TerminalTextSanitizer.SanitizeLine(dto.Title).Trim();
        string status = TerminalTextSanitizer.SanitizeLine(dto.Status).Trim();

        return new SessionListItem(
            dto.Id,
            title.Length == 0 ? "Untitled" : title,
            status.Length == 0 ? "Active" : status,
            dto.UpdatedAt,
            dto.EntryCount,
            dto.ForkedFromSessionId);
    }
}

internal enum CommandCenterOverlayKind
{
    None,
    Help,
    CommandPalette,
    SessionPicker,
    QuitConfirm,
    DiscardConfirm,
    HumanPrompt,
    ModelPicker,
}

internal enum CommandCenterUiUpdateKind
{
    RefreshAll,
    RefreshLog,
    RefreshHeader,
    RefreshSidebar,
    RefreshFooter,
    RefreshIncantations,
    FocusInput,
    FocusSessions,
    FocusTranscript,
}

internal sealed record CommandCenterUiUpdate(CommandCenterUiUpdateKind Kind);
