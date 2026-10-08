using System.Drawing;

using RetroDownfall.Arcanum.Cli.CommandCenter;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

/// <summary>
/// The key model on the real Terminal.Gui view tree, where a pure keymap test cannot reach: a line
/// break has to land at the caret, exactly as Enter used to put it there, rather than after the line.
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
}
