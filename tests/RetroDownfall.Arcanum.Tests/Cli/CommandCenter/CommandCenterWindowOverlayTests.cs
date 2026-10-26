using RetroDownfall.Arcanum.Cli.CommandCenter;

using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// Overlay behaviour that only shows up once the Terminal.Gui view tree is real: which control the
/// keyboard lands on, which rows the overlay is actually showing, and which list a refresh is
/// allowed to re-point. These are the parts a pure keymap test cannot pin.
/// </summary>
public sealed class CommandCenterWindowOverlayTests
{
    [Theory]
    [InlineData("界", 40, 120, 82)]
    [InlineData("語", 35, 100, 72)]
    [InlineData("A界", 25, 120, 77)]
    [InlineData("A", 80, 120, 82)]
    [InlineData("e\u0301", 40, 120, 60)]
    [InlineData("😀", 40, 140, 82)]
    [InlineData("界", 80, 200, 120)]
    [InlineData("界", 80, 80, 76)]
    public void Generic_overlay_frame_measures_display_cells_and_retains_width_caps(
        string glyph, int repetitions, int columns, int expectedWidth)
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(columns, 40);

        string line = string.Concat(Enumerable.Repeat(glyph, repetitions));

        window.ShowOverlay(CommandCenterOverlayKind.Help, [line], "Content", showFilter: false);

        Assert.True(window.Layout(new System.Drawing.Size(columns, 40)));

        Assert.Equal(expectedWidth, window.OverlayPane.Frame.Width);

        if (TerminalCellMetrics.MeasureWidth(line) <= expectedWidth - 2)
        {
            Assert.True(window.OverlayList.Viewport.Width >= TerminalCellMetrics.MeasureWidth(line));
        }

        window.ApplyAbsoluteLayout(80, 24);

        _ = window.Layout(new System.Drawing.Size(80, 24));

        Assert.Equal(Math.Min(76, expectedWidth), window.OverlayPane.Frame.Width);

        Assert.Equal(line, string.Concat(window.GetOverlayLinesSnapshot()));
    }

    [Theory]
    [InlineData(CommandCenterOverlayKind.Help)]
    [InlineData(CommandCenterOverlayKind.QuitConfirm)]
    internal void Generic_overlay_reflows_from_complete_content_when_terminal_width_changes(CommandCenterOverlayKind kind)
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 40);

        string content = new('A', 80);

        window.ShowOverlay(kind, [content], "Content", showFilter: false);

        Assert.True(window.Layout(new System.Drawing.Size(120, 40)));

        Assert.All(window.GetOverlayLinesSnapshot(), line =>
            Assert.InRange(TerminalCellMetrics.MeasureWidth(line), 0, window.OverlayList.Viewport.Width));

        window.ApplyAbsoluteLayout(80, 24);

        _ = window.Layout(new System.Drawing.Size(80, 24));

        Assert.All(window.GetOverlayLinesSnapshot(), line =>
            Assert.InRange(TerminalCellMetrics.MeasureWidth(line), 0, window.OverlayList.Viewport.Width));

        Assert.Equal(content, string.Concat(window.GetOverlayLinesSnapshot()));

        if (kind == CommandCenterOverlayKind.QuitConfirm)
        {
            Assert.Equal(string.Join('\n', window.GetOverlayLinesSnapshot()), window.OverlayBody.Text);
        }

        window.ApplyAbsoluteLayout(120, 40);

        _ = window.Layout(new System.Drawing.Size(120, 40));

        Assert.Equal(content, Assert.Single(window.GetOverlayLinesSnapshot()));

        Assert.Equal(82, window.OverlayPane.Frame.Width);
    }

    [Fact]
    internal void Resizing_a_picker_after_help_preserves_action_rows_and_selected_target()
    {
        using CommandCenterWindow window = new();

        window.ShowOverlay(CommandCenterOverlayKind.Help, [new string('H', 110)], "Help", showFilter: false);

        string longAction = new('A', 90);

        window.ShowOverlay(CommandCenterOverlayKind.CommandPalette, [longAction, "Quit"], "Commands", showFilter: false);

        window.MovePaletteSelection(1);

        foreach (int columns in new[] { 120, 80, 120 })
        {
            window.ApplyAbsoluteLayout(columns, 40);

            _ = window.Layout(new System.Drawing.Size(columns, 40));

            Assert.Equal(new[] { "  " + longAction, "> Quit" }, window.GetOverlayLinesSnapshot());

            Assert.Equal(1, window.GetOverlaySelectedIndex());
        }

        window.HideOverlayVisual();

        window.ApplyAbsoluteLayout(120, 40);

        _ = window.Layout(new System.Drawing.Size(120, 40));

        Assert.Empty(window.GetOverlayLinesSnapshot());

        Assert.Equal(60, window.OverlayPane.Frame.Width);

        window.ShowOverlay(CommandCenterOverlayKind.Help, ["Fresh help"], "Help", showFilter: false);

        _ = window.Layout(new System.Drawing.Size(120, 40));

        Assert.Equal("Fresh help", Assert.Single(window.GetOverlayLinesSnapshot()));

        Assert.Equal(60, window.OverlayPane.Frame.Width);
    }

    [Theory]
    [InlineData(CommandCenterOverlayKind.ModelPicker)]
    [InlineData(CommandCenterOverlayKind.SessionPicker)]
    internal void Dedicated_pickers_replace_prior_help_prose_before_resizing(CommandCenterOverlayKind kind)
    {
        using CommandCenterWindow window = new();

        string priorHelp = new('H', 110);

        window.ShowOverlay(CommandCenterOverlayKind.Help, [priorHelp], "Help", showFilter: false);

        CommandCenterState state = new(new SessionLogBuffer())
        {
            ModelChoices = CommandCenterModelPicker.Build(
            [
                new ModelInfoDto("text-model", "compat", "OpenAICompatible", "***", 128_000),
            ]),
            Sessions = Sessions,
            Overlay = kind,
            FocusRegion = CommandCenterFocusRegion.Sessions,
        };

        if (kind == CommandCenterOverlayKind.ModelPicker)
        {
            window.ShowModelPickerOverlay(state);
        }
        else
        {
            window.ShowSessionPickerOverlay();

            window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);
        }

        string[] expected = window.GetOverlayLinesSnapshot().ToArray();

        Assert.NotEmpty(expected);

        Assert.DoesNotContain(expected, line => line.Contains("HHHH", StringComparison.Ordinal));

        foreach (int columns in new[] { 120, 80, 120 })
        {
            window.ApplyAbsoluteLayout(columns, 40);

            _ = window.Layout(new System.Drawing.Size(columns, 40));

            Assert.Equal(expected, window.GetOverlayLinesSnapshot());
        }
    }

    [Theory]
    [InlineData(80, 24, 578)]
    [InlineData(120, 40, 578)]
    [InlineData(80, 24, 4000)]
    [InlineData(120, 40, 4000)]
    internal void A_long_human_question_is_complete_and_scrollable_while_the_answer_stays_visible(int columns, int rows, int questionLength)
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(columns, rows);

        const string decision = "Which option should I take: A, B or C?";

        string prefix = string.Concat(Enumerable.Repeat("Consider the different deployment constraints and explain the tradeoffs before choosing. ", 50));

        string question = prefix[..(questionLength - decision.Length)] + decision;

        window.ShowHumanPromptOverlay(question, "long-question", statusMessage: null);

        Assert.True(window.Layout(new System.Drawing.Size(columns, rows)));

        Assert.Contains("Which option should I take: A, B or C?", string.Join(string.Empty, window.GetOverlayLinesSnapshot()), StringComparison.Ordinal);

        Assert.True(window.OverlayList.Visible);

        Assert.True(window.OverlayList.Viewport.Height > 0);

        Assert.All(window.GetOverlayLinesSnapshot(), line =>
            Assert.InRange(TerminalCellMetrics.MeasureWidth(line), 0, window.OverlayList.Viewport.Width));

        Assert.True(window.OverlayAnswer.Visible);

        Assert.True(window.OverlayAnswer.Viewport.Height > 0);

        window.OverlayAnswer.Text = "8080";

        bool SawDecision() => string.Join(string.Empty, window.GetOverlayLinesSnapshot()
            .Skip(window.OverlayList.Viewport.Y).Take(window.OverlayList.Viewport.Height))
            .Contains("Which option should I take: A, B or C?", StringComparison.Ordinal);

        bool sawDecision = SawDecision();

        for (int index = 0; index < 20; index++)
        {
            _ = window.OverlayAnswer.NewKeyDownEvent(new Terminal.Gui.Input.Key(Terminal.Gui.Input.Key.PageDown.KeyCode));

            sawDecision |= SawDecision();
        }

        Assert.True(sawDecision, "The question's final choice must enter the visible question viewport.");

        for (int index = 0; index < 20; index++)
        {
            _ = window.OverlayAnswer.NewKeyDownEvent(new Terminal.Gui.Input.Key(Terminal.Gui.Input.Key.PageUp.KeyCode));
        }

        Assert.Equal(0, window.OverlayList.Viewport.Y);

        Assert.Equal("8080", window.GetHumanPromptAnswer());

        Assert.True(window.OverlayAnswer.HasFocus);

        if (columns > 80)
        {
            window.ApplyAbsoluteLayout(80, 24);

            _ = window.Layout(new System.Drawing.Size(80, 24));

            Assert.All(window.GetOverlayLinesSnapshot(), line =>
                Assert.InRange(TerminalCellMetrics.MeasureWidth(line), 0, window.OverlayList.Viewport.Width));

            Assert.Equal("8080", window.GetHumanPromptAnswer());
        }
    }

    /// <summary>
    /// Terminal.Gui refuses focus to a view whose SuperView cannot focus, so an unfocusable overlay
    /// pane makes the <c>ask_human</c> answer editor impossible to type into — every printable key
    /// is swallowed and the prompt can only ever submit an empty answer.
    /// </summary>
    [Fact]
    internal void The_ask_human_answer_editor_takes_focus_when_the_prompt_opens()
    {
        using var window = new CommandCenterWindow();

        window.ShowHumanPromptOverlay("What port should I use?", "prompt-1", statusMessage: null);

        Assert.True(window.OverlayAnswer.HasFocus);
    }

    /// <summary>
    /// The question is model text, and the overlay hands it to a terminal. Whatever the model put in it,
    /// the overlay shows only what a terminal would display rather than act on.
    /// </summary>
    [Fact]
    internal void The_ask_human_overlay_shows_the_question_without_terminal_control_sequences()
    {
        using var window = new CommandCenterWindow();

        window.ShowHumanPromptOverlay(
            "Which port?\u001b]52;c;AAAA\u0007 now\u001b[2J",
            "prompt\u001b[31m-1",
            statusMessage: "waiting\u001b]0;pwned\u0007");

        string shown = string.Join('\n', window.GetOverlayLinesSnapshot()) + "\n" + window.OverlayBody.Text;

        Assert.DoesNotContain('\u001b', shown);
        Assert.DoesNotContain('\u0007', shown);
        Assert.Contains("Which port? now", shown, StringComparison.Ordinal);
        Assert.Contains("prompt-1", shown, StringComparison.Ordinal);
        Assert.Contains("waiting", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every overlay list or body is built from text the Command Center did not write (a Session title,
    /// an MCP server name from a repository's own config), so the overlay is where it is stripped.
    /// </summary>
    [Fact]
    internal void Overlay_rows_and_title_are_stripped_of_terminal_control_sequences()
    {
        using var window = new CommandCenterWindow();

        window.ShowOverlay(
            CommandCenterOverlayKind.Help,
            ["server\u001b]52;c;AAAA\u0007-one", "tool\u001b[2J-two"],
            "Servers\u001b]0;pwned\u0007",
            showFilter: false);

        string shown = string.Join('\n', window.GetOverlayLinesSnapshot()) + "\n" + window.OverlayBody.Text;

        Assert.DoesNotContain('\u001b', shown);
        Assert.DoesNotContain('\u0007', shown);
        Assert.Contains("server-one", shown, StringComparison.Ordinal);
        Assert.Equal("Servers", window.OverlayPane.Title);
    }

    /// <summary>
    /// Same root cause, second surface: with an unfocusable overlay pane the model drop-down's
    /// type-ahead field never receives a keystroke, so the filter is dead and printable keys are
    /// discarded.
    /// </summary>
    [Fact]
    internal void The_model_drop_down_type_ahead_field_takes_focus_when_it_opens()
    {
        using var window = new CommandCenterWindow();

        var state = new CommandCenterState(new SessionLogBuffer())
        {
            ModelChoices = CommandCenterModelPicker.Build(
            [
                new ModelInfoDto("gpt-4o", "compat", "OpenAICompatible", "***", 128_000),
            ]),
        };

        window.ShowModelPickerOverlay(state);

        Assert.True(window.OverlayFilter.HasFocus);
    }

    /// <summary>
    /// The focus dot appended to the pane title must not stop the picker re-rendering: selection
    /// indices are computed against the filtered list, so a stale unfiltered display means Enter
    /// resumes a session the operator never highlighted.
    /// </summary>
    [Fact]
    internal void Filtering_the_session_picker_re_renders_it_after_the_focus_dot_is_added()
    {
        using var window = new CommandCenterWindow();

        var state = new CommandCenterState(new SessionLogBuffer())
        {
            Sessions = Sessions,
            Overlay = CommandCenterOverlayKind.SessionPicker,
            FocusRegion = CommandCenterFocusRegion.Sessions,
        };

        window.ShowSessionPickerOverlay();

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Assert.Equal(3, window.GetOverlayLinesSnapshot().Count);

        state.SessionFilter = "api";

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        string only = Assert.Single(window.GetOverlayLinesSnapshot());

        Assert.Contains("api", only, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shift+Tab into the Model region has to land somewhere. A header pane that cannot focus makes
    /// the drop-down unreachable by any input, leaving the documented control dead and the keys
    /// mutating the composer draft instead.
    /// </summary>
    [Fact]
    internal void The_header_model_drop_down_takes_focus_when_the_region_is_selected()
    {
        using var window = new CommandCenterWindow();

        window.ApplyAbsoluteLayout(120, 40);

        Assert.True(window.ModelSelectorVisible);

        window.FocusModelSelector();

        Assert.True(window.ModelSelector.HasFocus);

        Assert.Equal(CommandCenterFocusRegion.Model, window.ResolveFocusedRegion());
    }

    /// <summary>
    /// Same rule, third surface: Tab (or a bare PageUp from the composer) selects the Transcript
    /// region and calls <c>FocusLog</c>. A body pane that cannot focus makes that a no-op, so
    /// Terminal.Gui focus stays on the composer while the pane title claims the transcript owns it,
    /// and every printable key keeps mutating the draft.
    /// </summary>
    [Fact]
    internal void The_transcript_list_takes_focus_when_the_region_is_selected()
    {
        using var window = new CommandCenterWindow();

        window.ApplyAbsoluteLayout(120, 40);

        window.FocusLog();

        Assert.True(window.LogView.HasFocus);

        Assert.Equal(CommandCenterFocusRegion.Transcript, window.ResolveFocusedRegion());
    }

    /// <summary>
    /// The Incantations pane has the same contract as the transcript: selecting the region must move
    /// the keyboard there, or the ↑↓ handlers registered on the list are dead code.
    /// </summary>
    [Fact]
    internal void The_incantations_list_takes_focus_when_the_region_is_selected()
    {
        using var window = new CommandCenterWindow();

        window.ApplyAbsoluteLayout(120, 40);

        window.FocusIncantations();

        Assert.True(window.IncantationsView.HasFocus);

        Assert.Equal(CommandCenterFocusRegion.Incantations, window.ResolveFocusedRegion());
    }

    /// <summary>
    /// Making the header and overlay panes focusable must not cost the composer the focus it starts
    /// with — typing is what Command Center opens for.
    /// </summary>
    [Fact]
    internal void The_composer_still_holds_focus_at_startup()
    {
        using var window = new CommandCenterWindow();

        window.ApplyAbsoluteLayout(120, 40);

        window.FocusInput();

        Assert.True(window.Input.HasFocus);

        Assert.Equal(CommandCenterFocusRegion.Composer, window.ResolveFocusedRegion());
    }

    /// <summary>
    /// Every overlay shares one list source, so a sidebar refresh that pushes the session index into
    /// it while a shorter overlay is open takes the whole TUI down with an out-of-range selection.
    /// </summary>
    [Fact]
    internal void A_sidebar_refresh_does_not_index_a_shorter_overlay_out_of_range()
    {
        using var window = new CommandCenterWindow();

        SessionListItem[] many = BuildSessions(6);

        var state = new CommandCenterState(new SessionLogBuffer())
        {
            Sessions = many,
            SelectedSessionId = many[5].Id,
            Overlay = CommandCenterOverlayKind.CommandPalette,
            FocusRegion = CommandCenterFocusRegion.Overlay,
        };

        window.ShowOverlay(
            CommandCenterOverlayKind.CommandPalette,
            ["New Session", "Refresh", "Quit"],
            "Commands",
            showFilter: false);

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Assert.Equal(0, window.GetOverlaySelectedIndex());
    }

    /// <summary>
    /// The quieter half of the same defect: a long enough overlay does not throw, it silently moves
    /// the highlight, and one arrow key then commits a model chosen by a session row index.
    /// </summary>
    [Fact]
    internal void A_sidebar_refresh_does_not_re_point_the_model_drop_down()
    {
        using var window = new CommandCenterWindow();

        SessionListItem[] many = BuildSessions(6);

        var state = new CommandCenterState(new SessionLogBuffer())
        {
            Sessions = many,
            SelectedSessionId = many[5].Id,
            ModelChoices = CommandCenterModelPicker.Build(
            [
                new ModelInfoDto("m0", "compat", "OpenAICompatible", "***", 8_192),
                new ModelInfoDto("m1", "compat", "OpenAICompatible", "***", 8_192),
                new ModelInfoDto("m2", "compat", "OpenAICompatible", "***", 8_192),
                new ModelInfoDto("m3", "compat", "OpenAICompatible", "***", 8_192),
                new ModelInfoDto("m4", "compat", "OpenAICompatible", "***", 8_192),
                new ModelInfoDto("m5", "compat", "OpenAICompatible", "***", 8_192),
            ]),
            Overlay = CommandCenterOverlayKind.ModelPicker,
            FocusRegion = CommandCenterFocusRegion.Overlay,
        };

        window.ShowModelPickerOverlay(state);

        Assert.Equal(0, window.GetOverlaySelectedIndex());

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Assert.Equal(0, window.GetOverlaySelectedIndex());
    }

    /// <summary>
    /// The palette is a list of actions, so it has to be drawn as one: a Label draws no highlight,
    /// leaving the operator no way to see which entry Enter is about to run.
    /// </summary>
    [Fact]
    internal void The_command_palette_shows_which_row_enter_will_run()
    {
        using var window = new CommandCenterWindow();

        window.ShowOverlay(
            CommandCenterOverlayKind.CommandPalette,
            ["New Session", "Open Sessions", "Refresh", "Model List", "Quit"],
            "Commands",
            showFilter: false);

        Assert.True(window.OverlayList.Visible);

        Assert.Equal(0, window.GetOverlaySelectedIndex());
    }

    /// <summary>
    /// Palette movement is bounded by the palette, so every entry is reachable however many (or few)
    /// sessions happen to be open.
    /// </summary>
    [Fact]
    internal void Every_command_palette_entry_is_reachable_regardless_of_the_session_count()
    {
        using var window = new CommandCenterWindow();

        window.ShowOverlay(
            CommandCenterOverlayKind.CommandPalette,
            ["New Session", "Open Sessions", "Refresh", "Model List", "Quit"],
            "Commands",
            showFilter: false);

        window.MovePaletteSelection(4);

        Assert.Equal(4, window.GetOverlaySelectedIndex());

        window.MovePaletteSelection(9);

        Assert.Equal(4, window.GetOverlaySelectedIndex());

        window.MovePaletteSelection(-9);

        Assert.Equal(0, window.GetOverlaySelectedIndex());
    }

    private static SessionListItem[] BuildSessions(int count) =>
        [.. Enumerable
            .Range(0, count)
            .Select(static i => new SessionListItem(
                Guid.Parse($"{i:D8}-0000-0000-0000-000000000000"),
                $"session {i}",
                "Active",
                DateTimeOffset.UnixEpoch,
                i))];

    private static readonly SessionListItem[] Sessions =
    [
        new(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "api rewrite",
            "Active",
            DateTimeOffset.UnixEpoch,
            2),
        new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            "grimoire schema",
            "Active",
            DateTimeOffset.UnixEpoch,
            3),
        new(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "ward tuning",
            "Active",
            DateTimeOffset.UnixEpoch,
            4),
    ];
}
