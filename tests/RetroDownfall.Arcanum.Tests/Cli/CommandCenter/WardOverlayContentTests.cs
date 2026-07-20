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

        Assert.Equal(5, choices.Length);
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
}
