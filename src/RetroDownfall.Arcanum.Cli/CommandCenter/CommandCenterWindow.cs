using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Terminal.Gui view tree for Command Center v2: header, left sessions, transcript, composer, footer.
/// Absolute frames so Warp SIGWINCH resizes refill the screen.
/// </summary>
internal sealed class CommandCenterWindow : Window
{
    private const int SidebarWidth = 28;

    private const int FooterHeight = 1;

    private const int BorderedHeaderCompactHeight = 3;

    private const int BorderedHeaderWithBrandHeight = 2 + CommandCenterBrandBanner.BrandedContentRows + 1;

    private readonly ObservableCollection<string> _logLines = new();

    private readonly ObservableCollection<string> _incantationLines = new();

    private readonly List<Guid?> _logLineAnchors = new();

    private readonly List<string?> _incantationLineAnchors = new();

    private readonly ObservableCollection<string> _sessionLines = new();

    private readonly ObservableCollection<string> _overlayLines = new();

    private bool _overlayShowFilter;

    private bool _overlayHumanPrompt;

    private bool _followTail = true;

    private bool _incantationsFollowTail = true;

    private bool _refreshingLog;

    private bool _refreshingIncantations;

    private Guid? _preservedLogEntryId;

    private string? _preservedIncantationCallId;

    private int _cols = 80;

    private int _rows = 24;

    private int _logContentWidth = 76;

    private int _incantationContentWidth = 76;

    private int _preservedSelectedItem;

    private int _preservedLogEntryLineOffset;

    private int _preservedIncantationSelectedItem;

    private SessionLogBuffer? _boundLog;

    private IncantationStore? _boundIncantations;

    private IReadOnlyList<SessionListItem> _boundSessions = [];

    private bool _layoutInProgress;

    private bool _reserveComposerScrollbar;

    private bool _showContextTelemetry = true;

    private Action? _requestComposerLayout;

    public CommandCenterWindow()
    {
        Title = string.Empty;
        BorderStyle = LineStyle.None;
        Arrangement = ViewArrangement.Fixed;
        CanFocus = true;
        SchemeName = CommandCenterTheme.BaseScheme;

        LineStyle chrome = CommandCenterTheme.PaneBorderStyle;

        HeaderPane = new FrameView
        {
            // Empty: the ASCII brand mark + status line are enough; avoid a redundant border title.
            Title = string.Empty,
            BorderStyle = chrome,
            CanFocus = false,
            SchemeName = CommandCenterTheme.HeaderScheme,
        };

        Banner = new Label
        {
            Text = CommandCenterBrandBanner.AsText(),
            CanFocus = false,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = CommandCenterBrandBanner.RowCount,
            SchemeName = CommandCenterTheme.BannerScheme,
        };
        HeaderPane.Add(Banner);

        Rights = new Label
        {
            Text = CommandCenterBrandBanner.RightsBlurb,
            CanFocus = false,
            X = 0,
            Y = CommandCenterBrandBanner.RowCount,
            Width = Dim.Fill(),
            Height = 1,
            SchemeName = CommandCenterTheme.SidebarScheme,
        };
        HeaderPane.Add(Rights);

        Header = new Label
        {
            Text = string.Empty,
            CanFocus = false,
            X = 0,
            Y = CommandCenterBrandBanner.BrandedContentRows,
            Width = Dim.Fill(),
            Height = 1,
            SchemeName = CommandCenterTheme.HeaderScheme,
        };
        HeaderPane.Add(Header);

        SessionsPane = new FrameView
        {
            Title = "Sessions",
            BorderStyle = chrome,
            CanFocus = true,
            SchemeName = CommandCenterTheme.SidebarScheme,
        };

        SessionsView = new ListView
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.SidebarScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        SessionsView.SetSource(_sessionLines);
        SessionsPane.Add(SessionsView);

        ContextTelemetry = new TelemetryPane();

        ContextTelemetryFrame = new FrameView
        {
            Title = "Context",
            BorderStyle = chrome,
            CanFocus = false,
            SchemeName = CommandCenterTheme.SidebarScheme,
        };

        ContextTelemetryBody = new Label
        {
            Text = ContextTelemetry.Text,
            CanFocus = false,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.SidebarScheme,
            TextAlignment = Alignment.Start,
            VerticalTextAlignment = Alignment.Start,
        };

        ContextTelemetryFrame.Add(ContextTelemetryBody);

        TranscriptPane = new FrameView
        {
            Title = "Transcript",
            BorderStyle = chrome,
            CanFocus = false,
            SchemeName = CommandCenterTheme.SessionScheme,
        };

        LogView = new ListView
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.SessionScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        LogView.SetSource(_logLines);
        LogView.ValueChanged += (_, _) =>
        {
            if (!_refreshingLog)
            {
                SyncFollowTailFromSelection();
            }
        };
        TranscriptPane.Add(LogView);

        ThinkingLabel = new Label
        {
            Text = string.Empty,
            CanFocus = false,
            Visible = false,
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            SchemeName = CommandCenterTheme.HeaderScheme,
        };
        TranscriptPane.Add(ThinkingLabel);

        IncantationsPane = new FrameView
        {
            Title = "Incantations",
            BorderStyle = chrome,
            CanFocus = false,
            SchemeName = CommandCenterTheme.SessionScheme,
        };

        IncantationsView = new ListView
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.SessionScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        IncantationsView.SetSource(_incantationLines);
        IncantationsView.ValueChanged += (_, _) =>
        {
            if (!_refreshingIncantations)
            {
                SyncIncantationsFollowTailFromSelection();
            }
        };
        IncantationsPane.Add(IncantationsView);

#pragma warning disable CS0618 // TextView obsolete in TG 2.4.17; Command Center still uses it for multiline composer.
        Input = new TextView
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            BorderStyle = chrome,
            Title = "Composer",
            SchemeName = CommandCenterTheme.InputScheme,
        };
        ConfigureComposerTextView(Input);
#pragma warning restore CS0618
        Input.ContentsChanged += (_, _) =>
        {
            // Mark dirty via layout request only — ApplyAbsoluteLayout owns frame changes.
            _requestComposerLayout?.Invoke();
        };

        Footer = new Label
        {
            Text = string.Empty,
            CanFocus = false,
            SchemeName = CommandCenterTheme.SidebarScheme,
        };

        OverlayPane = new FrameView
        {
            Title = "Overlay",
            BorderStyle = chrome,
            CanFocus = false,
            Visible = false,
            SchemeName = CommandCenterTheme.OverlayScheme,
        };

        OverlayFilter = new TextField
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            Visible = false,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            SchemeName = CommandCenterTheme.OverlayScheme,
        };

        // Confirm dialogs (Ward, quit, discard): multiline Label so each choice is its own row.
        // ListView is reserved for filterable pickers (Sessions).
        OverlayBody = new Label
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            Visible = false,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.OverlayScheme,
            TextAlignment = Alignment.Start,
            VerticalTextAlignment = Alignment.Start,
        };

        OverlayList = new ListView
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            Visible = false,
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            SchemeName = CommandCenterTheme.OverlayScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        OverlayList.SetSource(_overlayLines);

#pragma warning disable CS0618 // TextView obsolete in TG 2.4.17; multiline answer for HumanPrompt.
        OverlayAnswer = new TextView
        {
            CanFocus = true,
            TabStop = TabBehavior.NoStop,
            Visible = false,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = HumanPromptOverlayContent.AnswerViewportRows,
            BorderStyle = LineStyle.Single,
            Title = "Answer",
            SchemeName = CommandCenterTheme.OverlayScheme,
        };
        ConfigureComposerTextView(OverlayAnswer);
#pragma warning restore CS0618

        OverlayPane.Add(OverlayFilter, OverlayBody, OverlayList, OverlayAnswer);

        Add(
            HeaderPane,
            SessionsPane,
            ContextTelemetryFrame,
            TranscriptPane,
            IncantationsPane,
            Input,
            Footer,
            OverlayPane);
        ApplyAbsoluteLayout(80, 24);
    }

    public FrameView HeaderPane { get; }

    public Label Banner { get; }

    public Label Rights { get; }

    public Label Header { get; }

    public Label ThinkingLabel { get; }

    public FrameView SessionsPane { get; }

    public ListView SessionsView { get; }

    public TelemetryPane ContextTelemetry { get; }

    public FrameView ContextTelemetryFrame { get; }

    public Label ContextTelemetryBody { get; }

    public FrameView TranscriptPane { get; }

    public ListView LogView { get; }

    public FrameView IncantationsPane { get; }

    public ListView IncantationsView { get; }

#pragma warning disable CS0618
    public TextView Input { get; }
#pragma warning restore CS0618

    public Label Footer { get; }

    public FrameView OverlayPane { get; }

    public TextField OverlayFilter { get; }

    public Label OverlayBody { get; }

    /// <summary>Multiline answer editor for <see cref="CommandCenterOverlayKind.HumanPrompt"/>.</summary>
#pragma warning disable CS0618
    public TextView OverlayAnswer { get; }
#pragma warning restore CS0618

    public ListView OverlayList { get; }

    public bool SidebarVisible { get; private set; } = true;

    public bool FollowTail => _followTail;

    public bool IncantationsFollowTail => _incantationsFollowTail;

    public bool IsLogFocused => LogView.HasFocus;

    public bool IsIncantationsFocused => IncantationsView.HasFocus;

    public bool IsSessionsFocused => SessionsView.HasFocus || (OverlayPane.Visible && OverlayList.HasFocus);

    public CommandCenterFocusRegion? ResolveFocusedRegion()
    {
        if (Input.HasFocus)
        {
            return CommandCenterFocusRegion.Composer;
        }

        if (SessionsView.HasFocus
            || (OverlayPane.Visible && OverlayFilter.Visible && OverlayFilter.HasFocus)
            || (OverlayPane.Visible && OverlayPane.Title is "Sessions" or "Sessions ●" && OverlayList.HasFocus))
        {
            return CommandCenterFocusRegion.Sessions;
        }

        if (OverlayPane.Visible && (OverlayList.HasFocus || OverlayBody.HasFocus || OverlayAnswer.HasFocus))
        {
            return CommandCenterFocusRegion.Overlay;
        }

        if (LogView.HasFocus)
        {
            return CommandCenterFocusRegion.Transcript;
        }

        if (IncantationsView.HasFocus)
        {
            return CommandCenterFocusRegion.Incantations;
        }

        return null;
    }

    public IReadOnlyList<string> GetLogLinesSnapshot() => _logLines.ToArray();

    public int GetSelectedLogIndex() =>
        _logLines.Count == 0 ? -1 : Math.Clamp(LogView.SelectedItem ?? 0, 0, _logLines.Count - 1);

    public Guid? GetSelectedTranscriptEntryId(CommandCenterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        int index = GetSelectedLogIndex();
        if (index < 0 || index >= _logLineAnchors.Count)
        {
            return null;
        }

        if (_logLineAnchors[index] is not { } anchor)
        {
            return null;
        }

        return state.Log.Snapshot().FirstOrDefault(entry => entry.Id == anchor)?.SourceEntryId;
    }

    public Guid? GetSelectedSessionId(CommandCenterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IReadOnlyList<SessionListItem> list = state.FilteredSessions;
        if (list.Count == 0)
        {
            return null;
        }

        ListView view = OverlayPane.Visible ? OverlayList : SessionsView;
        int index = Math.Clamp(view.SelectedItem ?? 0, 0, list.Count - 1);
        return list[index].Id;
    }

    public string GetComposerText()
    {
        // Prefer live model lines so typing/paste is reflected before Text is set; preserve blank lines.
        try
        {
            var lines = Input.GetAllLines();
            if (lines is { Count: > 0 })
            {
                return JoinComposerLines(lines);
            }
        }
        catch
        {
        }

        return Input.Text?.ToString() ?? string.Empty;
    }

    public bool ComposerHasText => !string.IsNullOrWhiteSpace(GetComposerText());

    public void ClearComposer()
    {
        Input.Text = string.Empty;
        if (!_layoutInProgress)
        {
            ApplyAbsoluteLayout(_cols, _rows);
        }
    }

    /// <summary>
    /// Host wires this so ContentsChanged only marks dirty and requests UI-thread layout
    /// (ApplyAbsoluteLayout owns frame changes).
    /// </summary>
    public void SetComposerLayoutRequest(Action? request) => _requestComposerLayout = request;

    public void InsertComposerNewLine()
    {
        Input.InsertText("\n");
        _requestComposerLayout?.Invoke();
    }

    public void WireResize(IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        RelayoutFromApp(app);
        app.ScreenChanged += (_, args) =>
        {
            app.Invoke(() =>
            {
                Rectangle screen = args.Value;
                int cols = Math.Max(screen.Width, app.Driver?.Cols ?? 0);
                int rows = Math.Max(screen.Height, app.Driver?.Rows ?? 0);
                if (cols < 2 || rows < 3)
                {
                    return;
                }

                ApplyAbsoluteLayout(cols, rows);
                SetNeedsDraw();
                app.ClearScreenNextIteration = true;
            });
        };
    }

    public void RelayoutFromApp(IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        (int cols, int rows) = CommandCenterApp.ResolveViewportSize(app);
        if (cols < 2)
        {
            cols = 80;
        }

        if (rows < 3)
        {
            rows = 24;
        }

        ApplyAbsoluteLayout(cols, rows);
    }

    public void ApplyState(
        CommandCenterState state,
        IApplication? app = null,
        bool forceFollowTail = false,
        CommandCenterUiUpdateKind kind = CommandCenterUiUpdateKind.RefreshAll)
    {
        ArgumentNullException.ThrowIfNull(state);

        void Apply()
        {
            if (kind is CommandCenterUiUpdateKind.RefreshAll
                or CommandCenterUiUpdateKind.RefreshHeader
                or CommandCenterUiUpdateKind.RefreshFooter)
            {
                Header.Text = TruncateToWidth(state.HeaderText, Math.Max(8, _cols - 4));
                Footer.Text = TruncateToWidth(state.FooterHints, Math.Max(8, _cols - 2));
                UpdateThinkingLabel(state);
            }

            if (kind is CommandCenterUiUpdateKind.RefreshAll or CommandCenterUiUpdateKind.RefreshSidebar)
            {
                RefreshSessionList(state);

                ContextTelemetry.UpdateContext(state.LastContextBreakdown);

                ContextTelemetryBody.Text = ContextTelemetry.Text;

                bool visibilityChanged = _showContextTelemetry != state.ShowTelemetryPane;

                _showContextTelemetry = state.ShowTelemetryPane;

                if (visibilityChanged)
                {
                    ApplyAbsoluteLayout(_cols, _rows);
                }
            }

            if (kind is CommandCenterUiUpdateKind.RefreshAll or CommandCenterUiUpdateKind.RefreshLog)
            {
                _boundLog = state.Log;
                if (forceFollowTail)
                {
                    _followTail = true;
                }

                RefreshLogLines(state.Log, preserveScroll: !forceFollowTail);
            }

            if (kind is CommandCenterUiUpdateKind.RefreshAll or CommandCenterUiUpdateKind.RefreshIncantations)
            {
                _boundIncantations = state.Incantations;
                RefreshIncantationLines(state.Incantations, preserveScroll: true);
            }

            UpdateFocusChrome(state);
            SyncOverlay(state);
        }

        if (app is not null)
        {
            app.Invoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    public void FocusInput(IApplication? app = null)
    {
        void Focus()
        {
            Input.SetFocus();
        }

        if (app is not null)
        {
            app.Invoke(Focus);
        }
        else
        {
            Focus();
        }
    }

    public void FocusLog(IApplication? app = null)
    {
        void Focus()
        {
            LogView.SetFocus();
            if (_logLines.Count == 0)
            {
                return;
            }

            if (_followTail)
            {
                int last = _logLines.Count - 1;
                LogView.SelectedItem = last;
            }

            EnsureLogSelectionVisible();
        }

        if (app is not null)
        {
            app.Invoke(Focus);
        }
        else
        {
            Focus();
        }
    }

    public void FocusIncantations(IApplication? app = null)
    {
        void Focus()
        {
            IncantationsView.SetFocus();
            if (_incantationLines.Count == 0)
            {
                return;
            }

            if (_incantationsFollowTail)
            {
                IncantationsView.SelectedItem = _incantationLines.Count - 1;
            }
        }

        if (app is not null)
        {
            app.Invoke(Focus);
        }
        else
        {
            Focus();
        }
    }

    public void FocusSessions(IApplication? app = null, bool forceOverlay = false)
    {
        void Focus()
        {
            if (!SidebarVisible || forceOverlay)
            {
                ShowSessionPickerOverlay();
                OverlayFilter.SetFocus();
                return;
            }

            HideOverlayVisual();
            SessionsPane.CanFocus = true;
            SessionsView.CanFocus = true;
            // Prefer the list itself so ↑↓/jk land on SessionsView.KeyDown.
            if (!SessionsView.HasFocus)
            {
                SessionsView.SetFocus();
            }
        }

        if (app is not null)
        {
            app.Invoke(Focus);
        }
        else
        {
            Focus();
        }
    }

    /// <summary>
    /// Ensures a concrete session row is selected when focusing the sidebar (Claude Code–style).
    /// </summary>
    public void EnsureSessionSelection(CommandCenterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IReadOnlyList<SessionListItem> list = state.FilteredSessions;
        if (list.Count == 0)
        {
            return;
        }

        int idx = 0;
        if (state.SelectedSessionId is { } selected)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id == selected)
                {
                    idx = i;
                    break;
                }
            }
        }
        else if (state.SessionId is { } current)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Id == current)
                {
                    idx = i;
                    break;
                }
            }
        }

        SessionsView.SelectedItem = idx;
        state.SelectedSessionId = list[idx].Id;
        try
        {
            SessionsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void ShowOverlay(CommandCenterOverlayKind kind, IReadOnlyList<string> lines, string title, bool showFilter)
    {
        _overlayHumanPrompt = false;
        OverlayAnswer.Visible = false;
        try
        {
            OverlayAnswer.Text = string.Empty;
        }
        catch
        {
        }

        OverlayPane.Title = title;
        OverlayPane.Visible = true;
        _overlayShowFilter = showFilter;
        OverlayFilter.Visible = showFilter;
        OverlayFilter.Text = string.Empty;

        // Size width from raw content, then wrap long lines so height expands and text stays readable.
        int longest = 0;
        foreach (string line in lines)
        {
            if (line.Length > longest)
            {
                longest = line.Length;
            }
        }

        int overlayW = OverlayLayout.MeasureWidth(_cols, longest);
        int innerWidth = Math.Max(8, overlayW - 2);
        IReadOnlyList<string> displayLines = showFilter
            ? lines
            : OverlayLayout.WrapLines(lines, innerWidth);

        _overlayLines.Clear();
        foreach (string line in displayLines)
        {
            _overlayLines.Add(line);
        }

        if (showFilter)
        {
            OverlayBody.Visible = false;
            OverlayBody.Text = string.Empty;
            OverlayList.Visible = true;
            OverlayList.Y = 1;
            OverlayList.Height = Dim.Fill(1);
            if (_overlayLines.Count > 0)
            {
                OverlayList.SelectedItem = 0;
            }
        }
        else
        {
            OverlayList.Visible = false;
            OverlayBody.Visible = true;
            OverlayBody.Y = 0;
            OverlayBody.Height = Dim.Fill();
            OverlayBody.Text = string.Join('\n', displayLines);
        }

        // Recompute frame to content height before focusing (half-screen default is too tall for confirms).
        ApplyAbsoluteLayout(_cols, _rows);

        if (showFilter)
        {
            OverlayFilter.SetFocus();
        }
        else
        {
            OverlayBody.SetFocus();
        }
    }

    /// <summary>
    /// Hard-modal HumanPrompt: question/promptId in the body Label; multiline answer TextView below.
    /// </summary>
    public void ShowHumanPromptOverlay(string question, string promptId, string? statusMessage)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(promptId);

        _overlayHumanPrompt = true;
        _overlayShowFilter = false;
        OverlayPane.Title = "Mage asks";
        OverlayPane.Visible = true;
        OverlayFilter.Visible = false;
        OverlayFilter.Text = string.Empty;
        OverlayList.Visible = false;

        List<string> lines =
        [
            question,
            string.Empty,
            $"promptId: {promptId}",
        ];
        lines.AddRange(HumanPromptOverlayContent.HintLines);
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            lines.Add(string.Empty);
            lines.Add(statusMessage!);
        }

        int longest = 0;
        foreach (string line in lines)
        {
            if (line.Length > longest)
            {
                longest = line.Length;
            }
        }

        int overlayW = OverlayLayout.MeasureWidth(_cols, longest);
        int innerWidth = Math.Max(8, overlayW - 2);
        IReadOnlyList<string> displayLines = OverlayLayout.WrapLines(lines, innerWidth);

        _overlayLines.Clear();
        foreach (string line in displayLines)
        {
            _overlayLines.Add(line);
        }

        int answerRows = HumanPromptOverlayContent.AnswerViewportRows;
        OverlayBody.Visible = true;
        OverlayBody.Y = 0;
        OverlayBody.Height = Math.Max(1, displayLines.Count);
        OverlayBody.Text = string.Join('\n', displayLines);

        OverlayAnswer.Visible = true;
        OverlayAnswer.Y = displayLines.Count;
        OverlayAnswer.Height = answerRows;
        OverlayAnswer.Width = Dim.Fill();

        ApplyAbsoluteLayout(_cols, _rows);
        OverlayAnswer.SetFocus();
    }

    public string GetHumanPromptAnswer()
    {
        try
        {
            var lines = OverlayAnswer.GetAllLines();
            if (lines is { Count: > 0 })
            {
                return JoinComposerLines(lines);
            }
        }
        catch
        {
        }

        return OverlayAnswer.Text?.ToString() ?? string.Empty;
    }

    public void ClearHumanPromptAnswer()
    {
        try
        {
            OverlayAnswer.Text = string.Empty;
        }
        catch
        {
        }
    }

    public void HideOverlayVisual()
    {
        OverlayPane.Visible = false;
        OverlayFilter.Visible = false;
        OverlayFilter.Text = string.Empty;
        OverlayBody.Visible = false;
        OverlayBody.Text = string.Empty;
        OverlayList.Visible = false;
        OverlayAnswer.Visible = false;
        try
        {
            OverlayAnswer.Text = string.Empty;
        }
        catch
        {
        }

        _overlayShowFilter = false;
        _overlayHumanPrompt = false;
        _overlayLines.Clear();
    }

    public void ShowSessionPickerOverlay()
    {
        _overlayHumanPrompt = false;
        OverlayAnswer.Visible = false;
        try
        {
            OverlayAnswer.Text = string.Empty;
        }
        catch
        {
        }

        OverlayPane.Title = "Sessions";
        OverlayPane.Visible = true;
        _overlayShowFilter = true;
        OverlayBody.Visible = false;
        OverlayBody.Text = string.Empty;
        OverlayFilter.Visible = true;
        OverlayList.Visible = true;
        OverlayList.Y = 1;
        OverlayList.Height = Dim.Fill(1);
        ApplyAbsoluteLayout(_cols, _rows);
        OverlayFilter.SetFocus();
    }

    public bool IsOverlayFocused =>
        OverlayPane.Visible
        && (OverlayList.HasFocus || OverlayFilter.HasFocus || OverlayBody.HasFocus || OverlayAnswer.HasFocus);

    public int GetOverlaySelectedIndex() =>
        _overlayLines.Count == 0 ? -1 : Math.Clamp(OverlayList.SelectedItem ?? 0, 0, _overlayLines.Count - 1);

    public void MoveSessionSelection(int delta, CommandCenterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        IReadOnlyList<SessionListItem> list = state.FilteredSessions;
        if (list.Count == 0)
        {
            return;
        }

        ListView view = OverlayPane.Visible ? OverlayList : SessionsView;
        int current = Math.Clamp(view.SelectedItem ?? 0, 0, list.Count - 1);
        int next = Math.Clamp(current + delta, 0, list.Count - 1);
        view.SelectedItem = next;
        state.SelectedSessionId = list[next].Id;
        try
        {
            view.EnsureSelectedItemVisible();
        }
        catch
        {
        }

        // Refresh > markers for the new selection without resetting focus.
        RefreshSessionList(state);
        UpdateFocusChrome(state);
    }

    public void ScrollLogUp(int lines = 1)
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        _followTail = false;
        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            LogView.MoveUp();
        }

        SyncFollowTailFromSelection();
        EnsureLogSelectionVisible();
    }

    public void ScrollLogDown(int lines = 1)
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            LogView.MoveDown();
        }

        SyncFollowTailFromSelection();
        EnsureLogSelectionVisible();
    }

    public void PageLogUp()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        _followTail = false;
        LogView.MovePageUp();
        SyncFollowTailFromSelection();
        EnsureLogSelectionVisible();
    }

    public void PageLogDown()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        LogView.MovePageDown();
        SyncFollowTailFromSelection();
        EnsureLogSelectionVisible();
    }

    public void ScrollLogHome()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        _followTail = false;
        LogView.MoveHome();
        SyncFollowTailFromSelection();
        EnsureLogSelectionVisible();
    }

    public void ScrollLogEnd()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        LogView.MoveEnd();
        _followTail = true;
        EnsureLogSelectionVisible();
    }

    public void ScrollIncantationsUp(int lines = 1)
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        _incantationsFollowTail = false;
        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            IncantationsView.MoveUp();
        }

        SyncIncantationsFollowTailFromSelection();
        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void ScrollIncantationsDown(int lines = 1)
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        for (int i = 0; i < Math.Max(1, lines); i++)
        {
            IncantationsView.MoveDown();
        }

        SyncIncantationsFollowTailFromSelection();
        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void PageIncantationsUp()
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        _incantationsFollowTail = false;
        IncantationsView.MovePageUp();
        SyncIncantationsFollowTailFromSelection();
        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void PageIncantationsDown()
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        IncantationsView.MovePageDown();
        SyncIncantationsFollowTailFromSelection();
        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void ScrollIncantationsHome()
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        _incantationsFollowTail = false;
        IncantationsView.MoveHome();
        SyncIncantationsFollowTailFromSelection();
        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void ScrollIncantationsEnd()
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        _incantationsFollowTail = true;
        IncantationsView.MoveEnd();
        SyncIncantationsFollowTailFromSelection();
        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    public void ApplyAbsoluteLayout(int cols, int rows)
    {
        if (_layoutInProgress)
        {
            return;
        }

        _layoutInProgress = true;
        try
        {
            ApplyAbsoluteLayoutCore(cols, rows);
        }
        finally
        {
            _layoutInProgress = false;
        }
    }

    private void ApplyAbsoluteLayoutCore(int cols, int rows)
    {
        _cols = Math.Max(cols, 2);
        _rows = Math.Max(rows, 3);

        X = 0;
        Y = 0;
        Width = _cols;
        Height = _rows;

        LineStyle chrome = CommandCenterTheme.PaneBorderStyle;
        HeaderPane.BorderStyle = chrome;
        SessionsPane.BorderStyle = chrome;
        ContextTelemetryFrame.BorderStyle = chrome;
        TranscriptPane.BorderStyle = chrome;
        Input.BorderStyle = chrome;
        OverlayPane.BorderStyle = chrome;

        bool showSidebar = _cols >= 100;
        SidebarVisible = showSidebar;
        SessionsPane.Visible = showSidebar;

        bool showBrand = CommandCenterBrandBanner.Fits(_cols, _rows);
        Banner.Visible = showBrand;
        Rights.Visible = showBrand;
        Banner.Text = showBrand ? CommandCenterBrandBanner.AsText() : string.Empty;
        Rights.Text = showBrand ? CommandCenterBrandBanner.RightsBlurb : string.Empty;

        int headerH = showBrand ? BorderedHeaderWithBrandHeight : BorderedHeaderCompactHeight;
        int footerH = FooterHeight;

        // First-pass viewport width estimate: full width minus left/right border.
        int composerOuterW = _cols;
        int viewportWidth = Math.Max(1, composerOuterW - ComposerLayout.BorderOverhead);
        if (_reserveComposerScrollbar)
        {
            viewportWidth = Math.Max(1, viewportWidth - ComposerLayout.ScrollbarReservation);
        }

        string composerText = GetComposerText();
        // Prefer the larger of cell-wrap count and TextView.Lines so we never under-size
        // while the caret is on a trailing blank line the string join might under-count.
        int lineFloor = 1;
        try
        {
            lineFloor = Math.Max(1, Input.Lines);
        }
        catch
        {
        }

        int wrapped = Math.Max(ComposerLayout.CountWrappedRows(composerText, Math.Max(1, viewportWidth)), lineFloor);
        ComposerLayoutResult measure = ComposerLayout.Measure(
            composerText,
            viewportWidth + (_reserveComposerScrollbar ? ComposerLayout.ScrollbarReservation : 0),
            _rows,
            headerH,
            footerH,
            wrappedRowCount: wrapped);

        // Stabilize scrollbar reservation to avoid width oscillation across layout passes.
        if (measure.ReserveScrollbar != _reserveComposerScrollbar)
        {
            _reserveComposerScrollbar = measure.ReserveScrollbar;
            viewportWidth = Math.Max(1, composerOuterW - ComposerLayout.BorderOverhead);
            if (_reserveComposerScrollbar)
            {
                viewportWidth = Math.Max(1, viewportWidth - ComposerLayout.ScrollbarReservation);
            }

            wrapped = Math.Max(
                ComposerLayout.CountWrappedRows(composerText, Math.Max(1, viewportWidth)),
                lineFloor);
            measure = ComposerLayout.Measure(
                composerText,
                viewportWidth + (_reserveComposerScrollbar ? ComposerLayout.ScrollbarReservation : 0),
                _rows,
                headerH,
                footerH,
                wrappedRowCount: wrapped);
            _reserveComposerScrollbar = measure.ReserveScrollbar;
        }

        int inputH = measure.InputHeight;

        if (headerH + inputH + footerH + ComposerLayout.MinBodyHeight > _rows)
        {
            showBrand = false;
            Banner.Visible = false;
            Rights.Visible = false;
            Banner.Text = string.Empty;
            Rights.Text = string.Empty;
            headerH = Math.Min(BorderedHeaderCompactHeight, Math.Max(3, _rows / 5));
            int tightWidth = Math.Max(1, _cols - ComposerLayout.BorderOverhead);
            wrapped = Math.Max(ComposerLayout.CountWrappedRows(composerText, tightWidth), lineFloor);
            measure = ComposerLayout.Measure(
                composerText,
                tightWidth,
                _rows,
                headerH,
                footerH,
                wrappedRowCount: wrapped);
            inputH = measure.InputHeight;
            _reserveComposerScrollbar = measure.ReserveScrollbar;
        }

        if (showBrand)
        {
            Banner.Y = 0;
            Banner.Height = CommandCenterBrandBanner.RowCount;
            Rights.Y = CommandCenterBrandBanner.RowCount;
            Rights.Height = 1;
            Header.Y = CommandCenterBrandBanner.BrandedContentRows;
        }
        else
        {
            Header.Y = 0;
        }

        Header.Height = 1;

        int bodyH = Math.Max(ComposerLayout.MinBodyHeight, _rows - headerH - inputH - footerH);
        // If body ate into composer due to MinBodyHeight, shrink composer content further.
        int maxInputFromBody = Math.Max(
            ComposerLayout.MinContentRows + ComposerLayout.BorderOverhead,
            _rows - headerH - footerH - ComposerLayout.MinBodyHeight);
        if (inputH > maxInputFromBody)
        {
            inputH = maxInputFromBody;
            bodyH = Math.Max(ComposerLayout.MinBodyHeight, _rows - headerH - inputH - footerH);
        }

        int sidebarW = showSidebar ? Math.Min(SidebarWidth, Math.Max(14, _cols / 4)) : 0;

        bool showContextTelemetry = showSidebar
            && _showContextTelemetry
            && bodyH >= ContextTelemetry.Height + ComposerLayout.MinTranscriptHeight;

        int contextTelemetryH = showContextTelemetry
            ? Math.Min(ContextTelemetry.Height, Math.Max(5, bodyH / 2))
            : 0;

        int sessionsH = Math.Max(1, bodyH - contextTelemetryH);

        int transcriptW = Math.Max(1, _cols - sidebarW);
        _logContentWidth = Math.Max(8, transcriptW - 3);
        _incantationContentWidth = _logContentWidth;

        int incantH = Math.Max(
            ComposerLayout.MinIncantationsHeight,
            bodyH / 3);
        int transcriptH = bodyH - incantH;
        if (transcriptH < ComposerLayout.MinTranscriptHeight)
        {
            transcriptH = ComposerLayout.MinTranscriptHeight;
            incantH = Math.Max(ComposerLayout.MinIncantationsHeight, bodyH - transcriptH);
        }

        if (incantH + transcriptH > bodyH)
        {
            incantH = Math.Max(ComposerLayout.MinIncantationsHeight, bodyH - ComposerLayout.MinTranscriptHeight);
            transcriptH = Math.Max(ComposerLayout.MinTranscriptHeight, bodyH - incantH);
        }

        HeaderPane.X = 0;
        HeaderPane.Y = 0;
        HeaderPane.Width = _cols;
        HeaderPane.Height = headerH;

        if (showSidebar)
        {
            SessionsPane.X = 0;
            SessionsPane.Y = headerH;
            SessionsPane.Width = sidebarW;
            SessionsPane.Height = sessionsH;
            SessionsPane.Visible = true;

            ContextTelemetryFrame.X = 0;

            ContextTelemetryFrame.Y = headerH + sessionsH;

            ContextTelemetryFrame.Width = sidebarW;

            ContextTelemetryFrame.Height = contextTelemetryH;

            ContextTelemetryFrame.Visible = showContextTelemetry;

            TranscriptPane.X = sidebarW;
            TranscriptPane.Y = headerH;
            TranscriptPane.Width = transcriptW;
            TranscriptPane.Height = transcriptH;

            IncantationsPane.X = sidebarW;
            IncantationsPane.Y = headerH + transcriptH;
            IncantationsPane.Width = transcriptW;
            IncantationsPane.Height = incantH;
        }
        else
        {
            SessionsPane.Visible = false;

            ContextTelemetryFrame.Visible = false;

            TranscriptPane.X = 0;
            TranscriptPane.Y = headerH;
            TranscriptPane.Width = _cols;
            TranscriptPane.Height = transcriptH;

            IncantationsPane.X = 0;
            IncantationsPane.Y = headerH + transcriptH;
            IncantationsPane.Width = _cols;
            IncantationsPane.Height = incantH;
        }

        Input.X = 0;
        Input.Y = headerH + bodyH;
        Input.Width = _cols;
        Input.Height = inputH;

        Footer.X = 0;
        Footer.Y = headerH + bodyH + inputH;
        Footer.Width = _cols;
        Footer.Height = footerH;

        int longestOverlay = 0;
        foreach (string line in _overlayLines)
        {
            if (line.Length > longestOverlay)
            {
                longestOverlay = line.Length;
            }
        }

        int overlayW = OverlayLayout.MeasureWidth(_cols, longestOverlay);
        int contentRows = _overlayLines.Count;
        if (_overlayHumanPrompt)
        {
            contentRows += HumanPromptOverlayContent.AnswerViewportRows;
        }

        int overlayH = OverlayPane.Visible
            ? OverlayLayout.MeasureHeight(
                contentRows,
                _overlayShowFilter || OverlayFilter.Visible,
                bodyH,
                _rows,
                headerH)
            : OverlayLayout.MinHeight;
        OverlayPane.X = Math.Max(0, (_cols - overlayW) / 2);
        OverlayPane.Y = Math.Max(headerH, (_rows - overlayH) / 2);
        OverlayPane.Width = overlayW;
        OverlayPane.Height = overlayH;

        if (_overlayHumanPrompt && OverlayAnswer.Visible)
        {
            int answerRows = HumanPromptOverlayContent.AnswerViewportRows;
            int bodyRows = Math.Max(1, overlayH - 2 - answerRows);
            OverlayBody.Height = bodyRows;
            OverlayAnswer.Y = bodyRows;
            OverlayAnswer.Height = answerRows;
        }

        if (_boundLog is not null)
        {
            RefreshLogLines(_boundLog, preserveScroll: true);
        }

        if (_boundIncantations is not null)
        {
            RefreshIncantationLines(_boundIncantations, preserveScroll: true);
        }

        // TextView may scroll to keep the caret visible while still at the old height
        // (e.g. 2nd newline with 2 content rows → Viewport.Y=1). After we grow to fit,
        // that offset sticks and hides the first line — pin to top when content fits.
        SyncComposerScroll(contentFits: !measure.ReserveScrollbar);

        try
        {
            SetNeedsLayout();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Configures the composer TextView for soft-wrap. Terminal.Gui couples
    /// <c>EnterKeyAddsLine=false</c> to <c>Multiline=false</c> and <c>WordWrap=false</c>,
    /// so Enter stays as newline and <b>Ctrl+Enter</b> sends via the keymap.
    /// </summary>
#pragma warning disable CS0618
    internal static void ConfigureComposerTextView(TextView input)
#pragma warning restore CS0618
    {
        ArgumentNullException.ThrowIfNull(input);
        input.Multiline = true;
        input.WordWrap = true;
        // Tab cycles Command Center panes — never insert \t into the composer.
        input.TabKeyAddsTab = false;
        // Leave EnterKeyAddsLine at its Multiline=true default (true). Setting it false
        // clears Multiline/WordWrap in Terminal.Gui 2.4.17.
    }

    private void SyncComposerScroll(bool contentFits)
    {
        try
        {
            if (contentFits)
            {
                // Pin top so a pre-grow caret scroll (Viewport.Y>0) cannot hide earlier lines.
                System.Drawing.Rectangle vp = Input.Viewport;
                if (vp.Y != 0 || vp.X != 0)
                {
                    Input.ScrollTo(System.Drawing.Point.Empty);
                }

                return;
            }

            Input.PositionCursor();
        }
        catch
        {
        }
    }

    private static string JoinComposerLines(System.Collections.IList lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                _ = sb.Append('\n');
            }

            if (lines[i] is System.Collections.Generic.List<Cell> cells)
            {
                foreach (Cell cell in cells)
                {
                    _ = sb.Append(cell.Grapheme);
                }
            }
            else if (lines[i] is string s)
            {
                _ = sb.Append(s);
            }
        }

        return sb.ToString();
    }

    private void RefreshSessionList(CommandCenterState state)
    {
        _boundSessions = state.FilteredSessions;
        _sessionLines.Clear();
        if (_boundSessions.Count == 0)
        {
            _sessionLines.Add("(no sessions yet)");
        }
        else
        {
            foreach (SessionListItem item in _boundSessions)
            {
                bool current = state.SessionId == item.Id || state.SelectedSessionId == item.Id;
                _sessionLines.Add(current ? $"> {item.DisplayLine}" : $"  {item.DisplayLine}");
            }
        }

        if (OverlayPane.Visible && OverlayPane.Title == "Sessions")
        {
            _overlayLines.Clear();
            foreach (string line in _sessionLines)
            {
                _overlayLines.Add(line);
            }
        }

        if (_boundSessions.Count > 0 && state.SelectedSessionId is { } selected)
        {
            int idx = -1;
            for (int i = 0; i < _boundSessions.Count; i++)
            {
                if (_boundSessions[i].Id == selected)
                {
                    idx = i;
                    break;
                }
            }

            if (idx >= 0)
            {
                SessionsView.SelectedItem = idx;
                if (OverlayPane.Visible)
                {
                    OverlayList.SelectedItem = idx;
                }
            }
        }
    }

    private void SyncOverlay(CommandCenterState state)
    {
        if (state.Overlay == CommandCenterOverlayKind.None && OverlayPane.Visible
            && OverlayPane.Title is not "Sessions")
        {
            // Host may leave session picker open; don't auto-hide Sessions overlay here.
        }
    }

    private void UpdateFocusChrome(CommandCenterState state)
    {
        SessionsPane.Title = state.FocusRegion == CommandCenterFocusRegion.Sessions
            ? "Sessions ●"
            : "Sessions";
        TranscriptPane.Title = state.FocusRegion == CommandCenterFocusRegion.Transcript
            ? "Transcript ●"
            : "Transcript";
        IncantationsPane.Title = state.FocusRegion == CommandCenterFocusRegion.Incantations
            ? "Incantations ●"
            : "Incantations";
        Input.Title = state.FocusRegion == CommandCenterFocusRegion.Composer
            ? "Composer ●  Ctrl+Enter send · Enter newline"
            : "Composer";

        if (OverlayPane.Visible && !string.IsNullOrEmpty(OverlayPane.Title?.ToString()))
        {
            string raw = OverlayPane.Title.ToString()!;
            string baseTitle = raw.EndsWith(" ●", StringComparison.Ordinal) ? raw[..^2] : raw;
            OverlayPane.Title = state.FocusRegion == CommandCenterFocusRegion.Overlay
                || (state.FocusRegion == CommandCenterFocusRegion.Sessions && baseTitle == "Sessions")
                ? $"{baseTitle} ●"
                : baseTitle;
        }
    }

    private void UpdateThinkingLabel(CommandCenterState state)
    {
        if (state.ThinkingActive)
        {
            ThinkingLabel.Visible = true;
            ThinkingLabel.Text = TruncateToWidth(state.ThinkingDisplay, Math.Max(8, _logContentWidth));
        }
        else
        {
            ThinkingLabel.Visible = false;
            ThinkingLabel.Text = string.Empty;
        }
    }

    private void RefreshLogLines(SessionLogBuffer log, bool preserveScroll)
    {
        if (preserveScroll && !_followTail)
        {
            int idx = Math.Clamp(LogView.SelectedItem ?? 0, 0, Math.Max(0, _logLineAnchors.Count - 1));
            if (_logLineAnchors.Count > 0)
            {
                _preservedLogEntryId = _logLineAnchors[idx];
                _preservedLogEntryLineOffset = 0;
                for (int i = idx - 1;
                     i >= 0 && _logLineAnchors[i] == _preservedLogEntryId;
                     i--)
                {
                    _preservedLogEntryLineOffset++;
                }
            }

            _preservedSelectedItem = idx;
        }

        _refreshingLog = true;
        try
        {
            log.CopyLinesTo(_logLines, _logLineAnchors, _logContentWidth);
            RestoreLogViewport();
        }
        finally
        {
            _refreshingLog = false;
        }
    }

    private void RefreshIncantationLines(IncantationStore store, bool preserveScroll)
    {
        if (preserveScroll && !_incantationsFollowTail)
        {
            int idx = Math.Clamp(
                IncantationsView.SelectedItem ?? 0,
                0,
                Math.Max(0, _incantationLineAnchors.Count - 1));
            if (_incantationLineAnchors.Count > 0)
            {
                _preservedIncantationCallId = _incantationLineAnchors[idx];
            }

            _preservedIncantationSelectedItem = idx;
        }

        _refreshingIncantations = true;
        try
        {
            store.CopyDisplayLinesTo(_incantationLines, _incantationLineAnchors, _incantationContentWidth);
            RestoreIncantationsViewport();
        }
        finally
        {
            _refreshingIncantations = false;
        }
    }

    private void RestoreLogViewport()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        if (_followTail)
        {
            int last = _logLines.Count - 1;
            LogView.SelectedItem = last;
            _preservedSelectedItem = last;
            _preservedLogEntryId = _logLineAnchors.Count > last ? _logLineAnchors[last] : null;
            _preservedLogEntryLineOffset = 0;
        }
        else if (_preservedLogEntryId is { } entryId)
        {
            int found = -1;
            for (int i = 0; i < _logLineAnchors.Count; i++)
            {
                if (_logLineAnchors[i] == entryId)
                {
                    found = i;
                    break;
                }
            }

            int idx;
            if (found >= 0)
            {
                idx = found;
                for (int offset = 0;
                     offset < _preservedLogEntryLineOffset
                     && idx + 1 < _logLineAnchors.Count
                     && _logLineAnchors[idx + 1] == entryId;
                     offset++)
                {
                    idx++;
                }
            }
            else
            {
                idx = Math.Clamp(_preservedSelectedItem, 0, _logLines.Count - 1);
            }

            LogView.SelectedItem = idx;
            _preservedSelectedItem = idx;
        }
        else
        {
            int clamped = Math.Clamp(_preservedSelectedItem, 0, _logLines.Count - 1);
            LogView.SelectedItem = clamped;
            _preservedSelectedItem = clamped;
        }

        EnsureLogSelectionVisible();
    }

    private void RestoreIncantationsViewport()
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        if (_incantationsFollowTail)
        {
            int last = _incantationLines.Count - 1;
            IncantationsView.SelectedItem = last;
            _preservedIncantationSelectedItem = last;
            _preservedIncantationCallId =
                _incantationLineAnchors.Count > last ? _incantationLineAnchors[last] : null;
        }
        else if (_preservedIncantationCallId is { } callId)
        {
            int found = -1;
            for (int i = 0; i < _incantationLineAnchors.Count; i++)
            {
                if (string.Equals(_incantationLineAnchors[i], callId, StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }

            int idx = found >= 0
                ? found
                : Math.Clamp(_preservedIncantationSelectedItem, 0, _incantationLines.Count - 1);
            IncantationsView.SelectedItem = idx;
            _preservedIncantationSelectedItem = idx;
        }
        else
        {
            int clamped = Math.Clamp(_preservedIncantationSelectedItem, 0, _incantationLines.Count - 1);
            IncantationsView.SelectedItem = clamped;
            _preservedIncantationSelectedItem = clamped;
        }

        try
        {
            IncantationsView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    private void SyncFollowTailFromSelection()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        int selected = LogView.SelectedItem ?? 0;
        _preservedSelectedItem = Math.Max(0, selected);
        if (selected >= 0 && selected < _logLineAnchors.Count)
        {
            _preservedLogEntryId = _logLineAnchors[selected];
        }

        _followTail = selected >= _logLines.Count - 1;
    }

    private void SyncIncantationsFollowTailFromSelection()
    {
        if (_incantationLines.Count == 0)
        {
            return;
        }

        int selected = IncantationsView.SelectedItem ?? 0;
        _preservedIncantationSelectedItem = Math.Max(0, selected);
        if (selected >= 0 && selected < _incantationLineAnchors.Count)
        {
            _preservedIncantationCallId = _incantationLineAnchors[selected];
        }

        _incantationsFollowTail = selected >= _incantationLines.Count - 1;
    }

    private void EnsureLogSelectionVisible()
    {
        try
        {
            LogView.EnsureSelectedItemVisible();
        }
        catch
        {
        }
    }

    private static string TruncateToWidth(string text, int width)
    {
        if (width < 2 || text.Length <= width)
        {
            return text;
        }

        return text[..(width - 1)] + "…";
    }
}
