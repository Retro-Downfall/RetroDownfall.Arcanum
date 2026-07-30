using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Mutable per-run Command Center state. Not a DI singleton.
/// </summary>
internal sealed class CommandCenterState
{
    public const int RecentSessionLimit = 40;

    public const int TranscriptEntryLimit = 200;

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

    public string? Model { get; set; }

    public bool ShowTelemetryPane { get; set; } = true;

    public Guid? CampaignId { get; set; }

    public ServeLaunchResult? ServeLaunch { get; set; }

    public string? HealthSummary { get; set; }

    public IReadOnlyList<McpServerInfo> McpServers { get; set; } = [];

    public long? ManaUsed { get; set; }

    public int? ManaLimit { get; set; }

    public ContextTokenBreakdown? LastContextBreakdown { get; set; }

    public HashSet<string> StagedAttachmentPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Bound session attachment ids staged via <c>/attachments add</c> for the next turn
    /// (<see cref="PingRequest.AttachmentReferences"/>).
    /// </summary>
    public HashSet<Guid> StagedAttachmentReferences { get; } = [];

    /// <summary>
    /// Forbidden Arts the operator chose "always allow" for this Command Center run (in-memory).
    /// </summary>
    public HashSet<string> SessionAllowedArts { get; } = new(StringComparer.OrdinalIgnoreCase);

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

    public CancellationTokenSource? TurnCts { get; set; }

    public bool RequestExit { get; set; }

    public int ExitCode { get; set; }

    public bool MonochromeTheme { get; set; }

    public string WorkingDirectory { get; set; } = Environment.CurrentDirectory;

    public IReadOnlyList<SessionListItem> Sessions { get; set; } = [];

    public string SessionFilter { get; set; } = string.Empty;

    public Guid? SelectedSessionId { get; set; }

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

            return FocusRegion switch
            {
                CommandCenterFocusRegion.Sessions or CommandCenterFocusRegion.Overlay
                    when Overlay is CommandCenterOverlayKind.SessionPicker or CommandCenterOverlayKind.None
                    => "↑↓/jk select · Enter resume · type to filter · Esc composer · Ctrl+R refresh · F1 help",
                CommandCenterFocusRegion.Transcript
                    => "↑↓ scroll · PgUp/PgDn · Home/End · Esc composer · Tab focus · F1 help",
                CommandCenterFocusRegion.Incantations
                    => "↑↓ scroll Incantations · PgUp/PgDn · Home/End · Esc composer · Tab focus · F1 help",
                _
                    => "Ctrl+Enter send · Enter newline · Ctrl+K commands · Ctrl+O sessions · Ctrl+N new · Ctrl+R refresh · Ctrl+C cancel · Ctrl+Q quit · F1 · Tab",
            };
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
        SelectedSessionId = null;
        LastContextBreakdown = null;
    }

    public void ApplySessionMeta(Guid id, string? title, string? status, int? entryCount)
    {
        SessionId = id;
        SelectedSessionId = id;
        SessionTitle = string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim();
        SessionStatus = string.IsNullOrWhiteSpace(status) ? "Active" : status.Trim();
        SessionEntryCount = entryCount;
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
        return $"Session: {title} · {shortId} · {status}";
    }
}

internal sealed record SessionListItem(
    Guid Id,
    string Title,
    string Status,
    DateTimeOffset UpdatedAt,
    int EntryCount)
{
    public string ShortId => Id.ToString("N")[..8];

    public string DisplayLine
    {
        get
        {
            string title = string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
            if (title.Length > 22)
            {
                title = title[..21] + "…";
            }

            return $"{title}  {ShortId}";
        }
    }

    public static SessionListItem FromSummary(SessionSummaryDto dto) =>
        new(
            dto.Id,
            string.IsNullOrWhiteSpace(dto.Title) ? "Untitled" : dto.Title!,
            string.IsNullOrWhiteSpace(dto.Status) ? "Active" : dto.Status,
            dto.UpdatedAt,
            dto.EntryCount);
}

internal enum CommandCenterOverlayKind
{
    None,
    Help,
    CommandPalette,
    SessionPicker,
    QuitConfirm,
    DiscardConfirm,
    WardConfirm,
    HumanPrompt,
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
