using RetroDownfall.Arcanum.Cli.CommandCenter;
using Xunit;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class WardOverlayContentTests
{
    [Fact]
    public void ChoiceLines_are_each_on_their_own_line()
    {
        string[] choices = WardOverlayContent.ChoiceLines
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        Assert.Equal(3, choices.Length);
        Assert.Contains(choices, static c => c.Contains("always allow", StringComparison.Ordinal));
        Assert.Contains(choices, static c => c.Contains("allow once", StringComparison.Ordinal));
        Assert.Contains(choices, static c => c.Contains("deny", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(choices, static c => c.Contains('|'));

        // Joining for the modal Label must preserve one choice per newline.
        string rendered = string.Join('\n', WardOverlayContent.ChoiceLines);
        string[] renderedLines = rendered.Split('\n');
        Assert.Equal(WardOverlayContent.ChoiceLines.Length, renderedLines.Length);
        Assert.Equal(WardOverlayContent.ChoiceLines, renderedLines);
    }

    /// <summary>
    /// While the Ward modal is up it owns the keyboard: every non-Ctrl, non-Alt, non-Tab key is
    /// swallowed, so no slash command can be typed. Worse, the advertised <c>/ward allow</c> and
    /// <c>/ward deny</c> both spell w-a-r-d, and the third keystroke — 'a' — is the always-allow
    /// binding, so an operator following the copy to DENY a Forbidden Art grants it for the whole
    /// session instead. A choice line may only name a key the modal actually handles.
    /// </summary>
    [Fact]
    public void No_choice_line_advertises_a_slash_command()
    {
        Assert.DoesNotContain(
            WardOverlayContent.ChoiceLines,
            static line => line.TrimStart().StartsWith('/'));
    }
}
