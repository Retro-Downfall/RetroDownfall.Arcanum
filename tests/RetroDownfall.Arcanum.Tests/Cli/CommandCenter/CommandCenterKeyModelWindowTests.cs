using System.Drawing;

using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The key model and the chrome that teaches it, on the real Terminal.Gui view tree where a pure keymap
/// test cannot reach: a line break lands at the caret rather than after the line, the footer keeps whole
/// hints, a short terminal spends its rows on the transcript rather than the logo, and F1's help scrolls
/// instead of being cut off.
/// </summary>
public sealed class CommandCenterKeyModelWindowTests
{
    /// <summary>
    /// <c>TextView.InsertText("\n")</c> appends an empty row after the current line instead of splitting
    /// it, so "a|b" became "ab" plus a blank row and the next key typed after the break went to the wrong
    /// line.
    /// </summary>
    [Fact]
    public void A_composer_line_break_splits_the_line_at_the_caret()
    {
        using CommandCenterWindow window = new();

        window.Layout(new Size(80, 24));

        window.Input.Text = "ab";

        window.Input.InsertionPoint = new Point(1, 0);

        window.InsertComposerNewLine();

        Assert.Equal("a\nb", window.GetComposerText());
    }

    [Fact]
    public void An_ask_human_line_break_splits_the_answer_at_the_caret()
    {
        using CommandCenterWindow window = new();

        window.ShowHumanPromptOverlay("Which port should I use?", "prompt-1", statusMessage: null);

        window.Layout(new Size(80, 24));

        window.OverlayAnswer.Text = "ab";

        window.OverlayAnswer.InsertionPoint = new Point(1, 0);

        window.InsertHumanPromptNewLine();

        Assert.Equal("a\nb", window.GetHumanPromptAnswer());
    }

    /// <summary>The ask_human prompt teaches the same keys as the composer.</summary>
    [Fact]
    public void The_ask_human_prompt_teaches_Enter_to_submit_and_Ctrl_J_for_a_new_line()
    {
        using CommandCenterWindow window = new();

        window.ShowHumanPromptOverlay("Which port should I use?", "prompt-1", statusMessage: null);

        string shown = window.OverlayBody.Text;

        Assert.Contains("Enter = submit answer", shown, StringComparison.Ordinal);

        Assert.Contains("Ctrl+J = new line", shown, StringComparison.Ordinal);

        Assert.DoesNotContain("Ctrl+Enter", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// The focused composer's title carries the composer's own two keys, so the footer below it can
    /// spend its one row on where to go next.
    /// </summary>
    [Fact]
    public void The_focused_composer_title_names_Enter_to_send_and_Ctrl_J_for_a_new_line()
    {
        using CommandCenterWindow window = new();

        CommandCenterState state = new(new SessionLogBuffer())
        {
            FocusRegion = CommandCenterFocusRegion.Composer,
        };

        window.ApplyState(state);

        Assert.Equal("Composer ●  Enter send · Ctrl+J newline", window.Input.Title);
    }

    /// <summary>
    /// The footer keeps whole hints in priority order and ends with F1 help, instead of a joined line cut
    /// off mid-hint at the terminal edge.
    /// </summary>
    [Theory]
    [InlineData(80, 24, CommandCenterHintBarTests.ComposerFooterAt80Columns)]
    [InlineData(120, 36, CommandCenterHintBarTests.ComposerFooterAt120Columns)]
    public void The_composer_footer_keeps_whole_hints_and_ends_with_F1(int cols, int rows, string expected)
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(cols, rows);

        CommandCenterState state = new(new SessionLogBuffer())
        {
            FocusRegion = CommandCenterFocusRegion.Composer,
        };

        window.ApplyState(state);

        Assert.Equal(expected, window.Footer.Text);
    }

    /// <summary>A message is not a hint list: it is shown on its own, truncated with an ellipsis.</summary>
    [Fact]
    public void A_footer_message_is_shown_alone_and_truncated_with_an_ellipsis()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(80, 24);

        CommandCenterState state = new(new SessionLogBuffer())
        {
            FooterHint = new string('x', 100),
        };

        window.ApplyState(state);

        Assert.Equal(new string('x', 77) + "…", window.Footer.Text);
    }

    /// <summary>
    /// At 80x24 the three-row logo and the rights line took four of 24 rows and left the transcript seven
    /// rows of text. Below 30 rows they collapse into the header's border title.
    /// </summary>
    [Fact]
    public void Below_30_rows_the_logo_collapses_into_the_header_border_title()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(80, 24);

        Assert.False(window.Banner.Visible);

        Assert.False(window.Rights.Visible);

        Assert.Equal(CommandCenterBrandBanner.CompactTitle, window.HeaderPane.Title);

        window.Layout(new Size(80, 24));

        Assert.Equal(3, window.HeaderPane.Frame.Height);

        Assert.True(
            window.TranscriptPane.Frame.Height >= 12,
            $"The transcript pane is {window.TranscriptPane.Frame.Height} rows tall at 80x24.");
    }

    [Fact]
    public void At_30_rows_and_more_the_logo_keeps_its_rows_and_the_border_title_stays_empty()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(120, 36);

        Assert.True(window.Banner.Visible);

        Assert.True(window.Rights.Visible);

        Assert.Equal(string.Empty, window.HeaderPane.Title);
    }

    /// <summary>
    /// F1 was a label clipped to the overlay frame: at 120x36 its last line was cut off, and at 80x24
    /// about a third of it. As a list it scrolls to every line.
    /// </summary>
    [Fact]
    public void The_F1_help_is_a_list_that_scrolls_to_its_last_line()
    {
        using CommandCenterWindow window = new();

        window.ApplyAbsoluteLayout(80, 24);

        window.ShowOverlay(
            CommandCenterOverlayKind.Help,
            CommandCenterHost.HelpOverlayLines,
            "Help · ↑↓ scroll · Esc close",
            showFilter: false);

        Assert.True(window.OverlayList.Visible);

        Assert.False(window.OverlayBody.Visible);

        IReadOnlyList<string> rows = window.GetOverlayLinesSnapshot();

        Assert.Contains($"ask_human: {CommandCenterGuidance.HumanPromptFooter}", rows);

        window.Layout(new Size(80, 24));

        Assert.True(
            window.OverlayList.Viewport.Height < rows.Count,
            $"The help ({rows.Count} rows) should not fit the {window.OverlayList.Viewport.Height}-row list at 80x24.");

        window.OverlayList.MoveEnd();

        Assert.Equal(rows.Count - 1, window.OverlayList.SelectedItem);

        Assert.True(window.OverlayList.Viewport.Y > 0, "Moving to the last help line should scroll the list.");
    }
}
