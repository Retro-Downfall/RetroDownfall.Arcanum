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
