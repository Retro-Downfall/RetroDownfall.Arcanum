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

    private const int BorderedInputHeight = 3;

    private readonly ObservableCollection<string> _logLines = new();

    private readonly ObservableCollection<string> _sessionLines = new();

    private readonly ObservableCollection<string> _overlayLines = new();

    private bool _followTail = true;

    private int _cols = 80;

    private int _rows = 24;

    private int _logContentWidth = 76;

    private int _preservedSelectedItem;

    private SessionLogBuffer? _boundLog;

    private IReadOnlyList<SessionListItem> _boundSessions = [];

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
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.SidebarScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        SessionsView.SetSource(_sessionLines);
        SessionsPane.Add(SessionsView);

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
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            SchemeName = CommandCenterTheme.SessionScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        LogView.SetSource(_logLines);
        LogView.ValueChanged += (_, _) => SyncFollowTailFromSelection();
        TranscriptPane.Add(LogView);

        Input = new TextField
        {
            CanFocus = true,
            BorderStyle = chrome,
            Title = "Composer",
            SchemeName = CommandCenterTheme.InputScheme,
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
            SchemeName = CommandCenterTheme.SessionScheme,
        };

        OverlayFilter = new TextField
        {
            CanFocus = true,
            Visible = false,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            SchemeName = CommandCenterTheme.InputScheme,
        };

        OverlayList = new ListView
        {
            CanFocus = true,
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            SchemeName = CommandCenterTheme.SessionScheme,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar,
        };
        OverlayList.SetSource(_overlayLines);
        OverlayPane.Add(OverlayFilter, OverlayList);

        Add(HeaderPane, SessionsPane, TranscriptPane, Input, Footer, OverlayPane);
        ApplyAbsoluteLayout(80, 24);
    }

    public FrameView HeaderPane { get; }

    public Label Banner { get; }

    public Label Rights { get; }

    public Label Header { get; }

    public FrameView SessionsPane { get; }

    public ListView SessionsView { get; }

    public FrameView TranscriptPane { get; }

    public ListView LogView { get; }

    public TextField Input { get; }

    public Label Footer { get; }

    public FrameView OverlayPane { get; }

    public TextField OverlayFilter { get; }

    public ListView OverlayList { get; }

    public bool SidebarVisible { get; private set; } = true;

    public bool FollowTail => _followTail;

    public bool IsLogFocused => LogView.HasFocus;

    public bool IsSessionsFocused => SessionsView.HasFocus || (OverlayPane.Visible && OverlayList.HasFocus);

    public IReadOnlyList<string> GetLogLinesSnapshot() => _logLines.ToArray();

    public int GetSelectedLogIndex() =>
        _logLines.Count == 0 ? -1 : Math.Clamp(LogView.SelectedItem ?? 0, 0, _logLines.Count - 1);

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

    public string GetComposerText() => Input.Text?.ToString() ?? string.Empty;

    public bool ComposerHasText => !string.IsNullOrWhiteSpace(GetComposerText());

    public void ClearComposer() => Input.Text = string.Empty;

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
            }

            if (kind is CommandCenterUiUpdateKind.RefreshAll or CommandCenterUiUpdateKind.RefreshSidebar)
            {
                RefreshSessionList(state);
            }

            if (kind is CommandCenterUiUpdateKind.RefreshAll or CommandCenterUiUpdateKind.RefreshLog)
            {
                _boundLog = state.Log;
                if (forceFollowTail)
                {
                    _followTail = true;
                }

                if (!_followTail)
                {
                    _preservedSelectedItem = Math.Max(0, LogView.SelectedItem ?? 0);
                }

                state.Log.CopyLinesTo(_logLines, _logContentWidth);
                RestoreLogViewport();
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
            HideOverlayVisual();
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
                // Keep follow-tail until the user scrolls away.
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
        OverlayPane.Title = title;
        OverlayPane.Visible = true;
        OverlayFilter.Visible = showFilter;
        OverlayFilter.Text = string.Empty;
        OverlayList.Y = showFilter ? 1 : 0;
        OverlayList.Height = showFilter ? Dim.Fill(1) : Dim.Fill();
        _overlayLines.Clear();
        foreach (string line in lines)
        {
            _overlayLines.Add(line);
        }

        if (_overlayLines.Count > 0)
        {
            OverlayList.SelectedItem = 0;
        }

        if (showFilter)
        {
            OverlayFilter.SetFocus();
        }
        else
        {
            OverlayList.SetFocus();
        }
    }

    public void HideOverlayVisual()
    {
        OverlayPane.Visible = false;
        OverlayFilter.Visible = false;
        OverlayFilter.Text = string.Empty;
        _overlayLines.Clear();
    }

    public void ShowSessionPickerOverlay()
    {
        OverlayPane.Title = "Sessions";
        OverlayPane.Visible = true;
        OverlayFilter.Visible = true;
        OverlayList.Y = 1;
        OverlayList.Height = Dim.Fill(1);
        OverlayFilter.SetFocus();
    }

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

    public void ApplyAbsoluteLayout(int cols, int rows)
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
        int inputH = BorderedInputHeight;
        int footerH = FooterHeight;

        if (headerH + inputH + footerH >= _rows - 2)
        {
            showBrand = false;
            Banner.Visible = false;
            Rights.Visible = false;
            Banner.Text = string.Empty;
            Rights.Text = string.Empty;
            headerH = Math.Min(BorderedHeaderCompactHeight, Math.Max(3, _rows / 5));
            inputH = Math.Min(BorderedInputHeight, Math.Max(3, _rows / 5));
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

        int bodyH = Math.Max(1, _rows - headerH - inputH - footerH);
        int sidebarW = showSidebar ? Math.Min(SidebarWidth, Math.Max(14, _cols / 4)) : 0;
        int transcriptW = Math.Max(1, _cols - sidebarW);
        _logContentWidth = Math.Max(8, transcriptW - 3);

        HeaderPane.X = 0;
        HeaderPane.Y = 0;
        HeaderPane.Width = _cols;
        HeaderPane.Height = headerH;

        if (showSidebar)
        {
            SessionsPane.X = 0;
            SessionsPane.Y = headerH;
            SessionsPane.Width = sidebarW;
            SessionsPane.Height = bodyH;

            TranscriptPane.X = sidebarW;
            TranscriptPane.Y = headerH;
            TranscriptPane.Width = transcriptW;
            TranscriptPane.Height = bodyH;
        }
        else
        {
            TranscriptPane.X = 0;
            TranscriptPane.Y = headerH;
            TranscriptPane.Width = _cols;
            TranscriptPane.Height = bodyH;
        }

        Input.X = 0;
        Input.Y = headerH + bodyH;
        Input.Width = _cols;
        Input.Height = inputH;

        Footer.X = 0;
        Footer.Y = headerH + bodyH + inputH;
        Footer.Width = _cols;
        Footer.Height = footerH;

        int overlayW = Math.Min(_cols - 4, 60);
        int overlayH = Math.Min(bodyH, Math.Max(8, _rows / 2));
        OverlayPane.X = Math.Max(0, (_cols - overlayW) / 2);
        OverlayPane.Y = Math.Max(headerH, (_rows - overlayH) / 2);
        OverlayPane.Width = overlayW;
        OverlayPane.Height = overlayH;

        if (_boundLog is not null)
        {
            _boundLog.CopyLinesTo(_logLines, _logContentWidth);
            RestoreLogViewport();
        }

        try
        {
            SetNeedsLayout();
        }
        catch
        {
        }
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
        Input.Title = state.FocusRegion == CommandCenterFocusRegion.Composer
            ? "Composer ●  Enter send"
            : "Composer";
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
        }
        else
        {
            int clamped = Math.Clamp(_preservedSelectedItem, 0, _logLines.Count - 1);
            LogView.SelectedItem = clamped;
            _preservedSelectedItem = clamped;
        }

        EnsureLogSelectionVisible();
    }

    private void SyncFollowTailFromSelection()
    {
        if (_logLines.Count == 0)
        {
            return;
        }

        int selected = LogView.SelectedItem ?? 0;
        _preservedSelectedItem = Math.Max(0, selected);
        _followTail = selected >= _logLines.Count - 1;
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
