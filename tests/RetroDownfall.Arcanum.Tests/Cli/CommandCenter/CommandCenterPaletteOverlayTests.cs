using System.Drawing;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Configuration;
using Terminal.Gui.Input;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The palette and the slash menu as the operator meets them: a filter field that has the keyboard,
/// a list tall enough to show its rows once Terminal.Gui resolves the layout, and rows that fit the
/// frame they are drawn in. These need the real view tree, so they lay it out the way the main loop
/// would rather than reading back the rows the window was handed.
/// </summary>
public sealed class CommandCenterPaletteOverlayTests
{
    [Fact]
    public void Ctrl_K_opens_a_focused_filter_over_every_action_with_room_to_show_them_all()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        window.ShowPaletteOverlay(PaletteState(CommandPaletteMode.Actions, string.Empty));

        Assert.True(window.OverlayFilter.Visible);

        Assert.True(window.OverlayFilter.HasFocus);

        Assert.Equal(15, window.GetOverlayLinesSnapshot().Count);

        Assert.True(window.Layout(new Size(120, 36)));

        Assert.True(
            window.OverlayList.Viewport.Height >= 15,
            $"The palette list shows {window.OverlayList.Viewport.Height} of its 15 rows.");

        Assert.Equal(0, window.OverlayList.Viewport.Y);

        Assert.True(window.OverlayList.Frame.Bottom <= window.OverlayPane.Viewport.Height);
    }

    [Fact]
    public void The_palette_frame_says_it_can_be_typed_into()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        window.ShowPaletteOverlay(PaletteState(CommandPaletteMode.Actions, string.Empty));

        Assert.StartsWith("Commands · type to filter", window.OverlayPane.Title, StringComparison.Ordinal);

        window.HideOverlayVisual();

        window.ShowPaletteOverlay(PaletteState(CommandPaletteMode.Slash, "/"));

        Assert.StartsWith("Slash commands · type to filter", window.OverlayPane.Title, StringComparison.Ordinal);
    }

    /// <summary>Typing narrows the list to what matches and puts the highlight on the best match.</summary>
    [Fact]
    public void Filtering_the_palette_leaves_the_matching_row_highlighted()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        CommandCenterState state = PaletteState(CommandPaletteMode.Actions, string.Empty);

        window.ShowPaletteOverlay(state);

        window.MovePaletteSelection(5);

        state.PaletteFilter = "model";

        window.RefreshPaletteList(state);

        string only = Assert.Single(window.GetOverlayLinesSnapshot());

        Assert.StartsWith("Choose Model", only, StringComparison.Ordinal);

        Assert.Equal(0, window.GetOverlaySelectedIndex());
    }

    [Fact]
    public void A_filter_that_matches_nothing_says_so_in_the_list()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        CommandCenterState state = PaletteState(CommandPaletteMode.Slash, "/zzz");

        window.ShowPaletteOverlay(state);

        Assert.Empty(state.FilteredPaletteEntries);

        string only = Assert.Single(window.GetOverlayLinesSnapshot());

        Assert.Equal(CommandPaletteCatalog.NoMatchingSlashCommand, only.TrimEnd());
    }

    [Fact]
    public void Every_slash_menu_row_fits_inside_the_overlay_on_an_80_column_terminal()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(80, 24);

        window.ShowPaletteOverlay(PaletteState(CommandPaletteMode.Slash, "/"));

        Assert.True(window.Layout(new Size(80, 24)));

        IReadOnlyList<string> rows = window.GetOverlayLinesSnapshot();

        Assert.Equal(SlashCommandRegistry.All.Count, rows.Count);

        int inner = window.OverlayPane.Viewport.Width;

        int listCells = window.OverlayList.Viewport.Width;

        Assert.All(rows, row =>
        {
            Assert.True(TerminalCellMetrics.MeasureWidth(row) <= inner, $"`{row}` is wider than the {inner}-cell overlay.");

            Assert.True(
                TerminalCellMetrics.MeasureWidth(row.TrimEnd()) <= listCells,
                $"`{row.TrimEnd()}` runs under the scroll bar of a {listCells}-cell list.");
        });
    }

    /// <summary>
    /// The palette's filter field is part of the palette. Reading it as the Sessions region sends
    /// focus cycling and the footer to the sessions list while the palette is still open.
    /// </summary>
    [Fact]
    public void A_focused_filter_belongs_to_the_overlay_it_filters()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        window.ShowPaletteOverlay(PaletteState(CommandPaletteMode.Actions, string.Empty));

        Assert.True(window.OverlayFilter.HasFocus);

        Assert.Equal(CommandCenterFocusRegion.Overlay, window.ResolveFocusedRegion());

        window.HideOverlayVisual();

        window.ShowSessionPickerOverlay();

        Assert.True(window.OverlayFilter.HasFocus);

        Assert.Equal(CommandCenterFocusRegion.Sessions, window.ResolveFocusedRegion());
    }

    /// <summary>The keys typed after <c>/</c> extend the menu's filter rather than replacing it.</summary>
    [Fact]
    public void The_slash_menu_filter_keeps_the_slash_and_takes_the_next_key_after_it()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        window.ShowPaletteOverlay(PaletteState(CommandPaletteMode.Slash, "/"));

        Assert.True(window.NewKeyDownEvent(Key.M));

        Assert.Equal("/m", window.OverlayFilter.Text);
    }

    /// <summary>A completed command line leaves the caret after it, so the argument typed next follows it.</summary>
    [Fact]
    public void Text_handed_to_the_composer_is_continued_from_its_end()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        window.FocusInput();

        window.SetComposerText("/model ");

        Assert.Equal("/model ", window.GetComposerText());

        Assert.True(window.NewKeyDownEvent(Key.G));

        Assert.Equal("/model g", window.GetComposerText());
    }

    /// <summary>
    /// <c>/</c> opens the menu inside the key handler, not on a later main-loop turn, so the keys
    /// typed straight after it land in the menu's filter instead of the composer.
    /// </summary>
    [Fact]
    public void Slash_in_an_empty_composer_opens_the_slash_menu_before_the_next_key()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        Key slash = new('/');

        Assert.True(CommandCenterHost.TryOpenSlashMenu(slash, CommandCenterFocusRegion.Composer, state, window));

        Assert.True(slash.Handled);

        Assert.Equal(CommandCenterOverlayKind.CommandPalette, state.Overlay);

        Assert.Equal(CommandPaletteMode.Slash, state.PaletteMode);

        Assert.Equal(CommandCenterFocusRegion.Overlay, state.FocusRegion);

        Assert.True(window.OverlayFilter.HasFocus);

        Assert.Equal("/", window.OverlayFilter.Text);

        Assert.Equal(SlashCommandRegistry.All.Count, window.GetOverlayLinesSnapshot().Count);

        Assert.Equal(string.Empty, window.GetComposerText());
    }

    [Fact]
    public void Slash_is_ordinary_text_once_the_composer_holds_anything()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        window.SetComposerText(" ");

        Assert.False(CommandCenterHost.TryOpenSlashMenu(new Key('/'), CommandCenterFocusRegion.Composer, state, window));

        Assert.Equal(CommandCenterOverlayKind.None, state.Overlay);

        Assert.False(window.OverlayPane.Visible);
    }

    [Fact]
    public void Slash_opens_nothing_outside_the_composer_or_over_another_overlay_or_with_a_modifier()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        Assert.False(CommandCenterHost.TryOpenSlashMenu(new Key('/'), CommandCenterFocusRegion.Sessions, state, window));

        Assert.False(CommandCenterHost.TryOpenSlashMenu(new Key('/').WithCtrl, CommandCenterFocusRegion.Composer, state, window));

        Assert.False(CommandCenterHost.TryOpenSlashMenu(new Key('/').WithAlt, CommandCenterFocusRegion.Composer, state, window));

        state.Overlay = CommandCenterOverlayKind.Help;

        Assert.False(CommandCenterHost.TryOpenSlashMenu(new Key('/'), CommandCenterFocusRegion.Composer, state, window));

        Assert.False(window.OverlayPane.Visible);
    }

    /// <summary>
    /// The list narrows as each key is inserted, so an Enter typed in the same burst as <c>/mod</c>
    /// runs the row the operator sees rather than the row that was highlighted before the burst.
    /// </summary>
    [Fact]
    public void Typing_into_the_slash_menu_narrows_it_before_the_next_key_is_read()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        List<Action> deferred = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Type(window, Key.M, Key.O, Key.D);

        Assert.Equal("/mod", state.PaletteFilter);

        string only = Assert.Single(window.GetOverlayLinesSnapshot());

        Assert.StartsWith("/model", only, StringComparison.Ordinal);

        Assert.Empty(deferred);

        Assert.False(CommandCenterHost.CommitSlashMenu(state, window));

        Assert.Equal("/model ", window.GetComposerText());

        Assert.Equal(CommandCenterOverlayKind.None, state.Overlay);

        Assert.True(window.Input.HasFocus);
    }

    [Fact]
    public void Enter_on_a_command_without_arguments_leaves_it_in_the_composer_to_be_sent()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Type(window, Key.H, Key.E);

        Assert.True(CommandCenterHost.CommitSlashMenu(state, window));

        Assert.Equal("/help", window.GetComposerText());

        Assert.False(window.OverlayPane.Visible);
    }

    /// <summary>With nothing matching, Enter sends the typed line so the parser's did-you-mean can answer it.</summary>
    [Fact]
    public void Enter_with_no_match_leaves_the_typed_line_in_the_composer_to_be_sent()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Type(window, Key.Z, Key.Z);

        Assert.Null(CommandCenterHost.SelectedPaletteEntry(state, window));

        Assert.True(CommandCenterHost.CommitSlashMenu(state, window));

        Assert.Equal("/zz", window.GetComposerText());
    }

    [Fact]
    public void A_space_hands_the_slash_line_to_the_composer_so_the_argument_follows_it()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Type(window, Key.M, Key.O, Key.D, Key.E, Key.L);

        Key space = Key.Space;

        Assert.True(CommandCenterHost.TryHandleSlashMenuKey(space, state, window));

        Assert.True(space.Handled);

        Assert.Equal("/model ", window.GetComposerText());

        Assert.Equal(CommandCenterOverlayKind.None, state.Overlay);

        Assert.Equal(CommandCenterFocusRegion.Composer, state.FocusRegion);

        Assert.True(window.Input.HasFocus);

        Assert.True(window.NewKeyDownEvent(Key.G));

        Assert.Equal("/model g", window.GetComposerText());
    }

    [Fact]
    public void Backspace_past_the_slash_returns_to_an_empty_composer()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Assert.True(CommandCenterHost.TryHandleSlashMenuKey(Key.Backspace, state, window));

        Assert.Equal(string.Empty, window.GetComposerText());

        Assert.False(window.OverlayPane.Visible);

        Assert.True(window.Input.HasFocus);
    }

    [Fact]
    public void Backspace_inside_the_name_stays_in_the_slash_menu()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Type(window, Key.M, Key.O);

        Assert.False(CommandCenterHost.TryHandleSlashMenuKey(Key.Backspace, state, window));

        Assert.Equal(CommandCenterOverlayKind.CommandPalette, state.Overlay);
    }

    [Fact]
    public void Escape_returns_what_was_typed_to_the_composer()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        Type(window, Key.M, Key.O);

        Assert.True(CommandCenterHost.TryHandleSlashMenuKey(Key.Esc, state, window));

        Assert.Equal("/mo", window.GetComposerText());

        Assert.False(window.OverlayPane.Visible);
    }

    /// <summary>The palette's own keys keep their ordinary meaning: a space there is filter text.</summary>
    [Fact]
    public void Slash_menu_keys_mean_nothing_special_in_the_Ctrl_K_palette()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Actions, string.Empty);

        Assert.False(CommandCenterHost.TryHandleSlashMenuKey(Key.Space, state, window));

        Assert.False(CommandCenterHost.TryHandleSlashMenuKey(Key.Esc, state, window));

        Assert.Equal(CommandCenterOverlayKind.CommandPalette, state.Overlay);
    }

    /// <summary>
    /// A paste is not a key, so the space it carries is never seen as one. The line it leaves goes to
    /// the composer once the field has finished inserting it, instead of being completed over.
    /// </summary>
    [Fact]
    public void A_pasted_argument_hands_the_slash_line_to_the_composer_after_the_paste()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        List<Action> deferred = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Slash, "/");

        window.OverlayFilter.Text = "/model gemma4:e4b";

        Assert.Equal(CommandCenterOverlayKind.CommandPalette, state.Overlay);

        Action handOff = Assert.Single(deferred);

        handOff();

        Assert.Equal("/model gemma4:e4b", window.GetComposerText());

        Assert.Equal(CommandCenterOverlayKind.None, state.Overlay);
    }

    [Fact]
    public void The_palette_s_Slash_Commands_entry_opens_the_slash_menu_over_an_empty_composer()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        CommandCenterHost.OpenSlashMenuFromPalette(state, window);

        Assert.Equal(CommandCenterOverlayKind.CommandPalette, state.Overlay);

        Assert.Equal(CommandPaletteMode.Slash, state.PaletteMode);

        Assert.Equal("/", window.OverlayFilter.Text);
    }

    /// <summary>
    /// Every way out of the slash menu writes its line into the composer, so opening it over a draft
    /// would let the first key that leaves the menu overwrite the draft. It says why instead.
    /// </summary>
    [Fact]
    public void The_palette_s_Slash_Commands_entry_never_opens_over_a_draft()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        window.SetComposerText("half a question");

        CommandCenterHost.OpenSlashMenuFromPalette(state, window);

        Assert.Equal(CommandCenterOverlayKind.None, state.Overlay);

        Assert.False(window.OverlayPane.Visible);

        Assert.Equal("half a question", window.GetComposerText());

        Assert.Equal(CommandCenterHost.SlashMenuNeedsEmptyComposer, state.FooterHint);
    }

    [Fact]
    public void Closing_the_palette_hands_focus_back_to_the_composer_at_once()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Actions, "ref");

        CommandCenterHost.ClosePaletteNow(state, window, composerText: null);

        Assert.False(window.OverlayPane.Visible);

        Assert.True(window.Input.HasFocus);

        Assert.Equal(CommandCenterOverlayKind.None, state.Overlay);

        Assert.Equal(CommandCenterFocusRegion.Composer, state.FocusRegion);

        Assert.Equal(string.Empty, state.PaletteFilter);
    }

    [Fact]
    public void Enter_in_the_palette_runs_the_highlighted_action()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        CommandCenterHost.OpenPaletteNow(state, window, CommandPaletteMode.Actions, string.Empty);

        Type(window, Key.C, Key.H, Key.O);

        CommandPaletteEntry? selected = CommandCenterHost.SelectedPaletteEntry(state, window);

        Assert.NotNull(selected);

        Assert.Equal(CommandPaletteTarget.ChooseModel, selected.Target);
    }

    /// <summary>
    /// The model and session pickers share the filter field, so they narrow as each key is inserted
    /// too, rather than one main-loop turn later.
    /// </summary>
    [Fact]
    public void The_model_and_session_pickers_narrow_as_each_key_is_inserted()
    {
        using CommandCenterWindow window = ComposerWindow(out CommandCenterState state);

        _ = WireFilter(window, state);

        state.ModelChoices = CommandCenterModelPicker.Build(
        [
            new ModelInfoDto("ornith1.5:35b", "Ollama", "Ollama", "***", 8_192),
            new ModelInfoDto("gemma4:e4b", "Ollama", "Ollama", "***", 8_192),
        ]);

        state.Overlay = CommandCenterOverlayKind.ModelPicker;

        window.ShowModelPickerOverlay(state);

        Type(window, Key.G);

        Assert.Equal("g", state.ModelFilter);

        Assert.Contains("gemma4:e4b", Assert.Single(window.GetOverlayLinesSnapshot()), StringComparison.Ordinal);

        state.Overlay = CommandCenterOverlayKind.None;

        window.HideOverlayVisual();

        state.Sessions =
        [
            new SessionListItem(Guid.NewGuid(), "api rewrite", "Active", DateTimeOffset.UnixEpoch, 2),
            new SessionListItem(Guid.NewGuid(), "grimoire schema", "Active", DateTimeOffset.UnixEpoch, 3),
        ];

        state.Overlay = CommandCenterOverlayKind.SessionPicker;

        window.ShowSessionPickerOverlay();

        window.ApplyState(state, kind: CommandCenterUiUpdateKind.RefreshSidebar);

        Type(window, Key.A, Key.P, Key.I);

        Assert.Equal("api", state.SessionFilter);

        Assert.Contains("api rewrite", Assert.Single(window.GetOverlayLinesSnapshot()), StringComparison.Ordinal);
    }

    private static CommandCenterWindow ComposerWindow(out CommandCenterState state)
    {
        CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        window.FocusInput();

        state = new CommandCenterState(new SessionLogBuffer());

        return window;
    }

    /// <summary>Subscribes the filter the way the host does, keeping deferred work for the test to run.</summary>
    private static List<Action> WireFilter(CommandCenterWindow window, CommandCenterState state)
    {
        List<Action> deferred = [];

        window.OverlayFilter.TextChanged += (_, _) => CommandCenterHost.ApplyOverlayFilterText(state, window, deferred.Add);

        return deferred;
    }

    private static void Type(CommandCenterWindow window, params Key[] keys)
    {
        foreach (Key key in keys)
        {
            Assert.True(window.NewKeyDownEvent(key), $"{key} was not taken by the focused view.");
        }
    }

    private static CommandCenterState PaletteState(CommandPaletteMode mode, string filter) =>
        new(new SessionLogBuffer())
        {
            Overlay = CommandCenterOverlayKind.CommandPalette,
            FocusRegion = CommandCenterFocusRegion.Overlay,
            PaletteMode = mode,
            PaletteFilter = filter,
        };
}
