using System.Drawing;
using RetroDownfall.Arcanum.Cli.CommandCenter;
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

    private static CommandCenterState PaletteState(CommandPaletteMode mode, string filter) =>
        new(new SessionLogBuffer())
        {
            Overlay = CommandCenterOverlayKind.CommandPalette,
            FocusRegion = CommandCenterFocusRegion.Overlay,
            PaletteMode = mode,
            PaletteFilter = filter,
        };
}
