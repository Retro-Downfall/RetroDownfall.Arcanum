using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using Spectre.Console;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// A listing prints an identifier in full and never wraps it, so at 80 columns the rest of the row has to
/// give way in a way the operator can read: which columns are left out is decided by what fits, not by
/// Spectre squeezing every column down to a few characters.
/// </summary>
public sealed class ListingTableTests
{
    private const string Id = "33333333-3333-3333-3333-333333333333";

    private const string Campaign = "55555555-5555-4555-8555-555555555555";

    private sealed record Row(string Id, string Goal, string Status, string Campaign, string Updated);

    private static readonly IReadOnlyList<ListingColumn<Row>> Columns =
    [
        new("ID", static row => row.Id, ListingColumnRole.Fixed),
        new("Goal", static row => row.Goal, ListingColumnRole.Flexible),
        new("Status", static row => row.Status, ListingColumnRole.Fixed, Muted: true),
        new("Campaign", static row => row.Campaign, ListingColumnRole.Optional, Muted: true, DropOrder: 2),
        new("Updated", static row => row.Updated, ListingColumnRole.Optional, Muted: true, DropOrder: 1),
    ];

    private static readonly Row Wide = new(
        Id,
        "Refactor the nightly import so that a failed row no longer stops the whole batch",
        "Running",
        Campaign,
        "2026-07-02 12:00:00Z");

    private static IThemePalette Palette() =>
        new ConfiguredThemePalette(new ThemeSemanticColors(), new ThemeSemanticColors());

    private static string Render(ListingTableResult built, int width)
    {
        TestConsole console = new TestConsole().Width(width);

        console.Write(built.Table);

        return console.Output;
    }

    /// <summary>
    /// Listing cells carry text the operator did not write (a goal, a memory, a title), and Markup escaping
    /// does not remove the control sequences a terminal acts on, so every cell is stripped.
    /// </summary>
    [Fact]
    public void Cells_are_stripped_of_terminal_controls()
    {
        Row hostile = new(Id, "ok\u001b]52;c;QUFBQQ==\u0007title\u001b[2J\u009b", "Running", Campaign, "2026-07-02 12:00:00Z");

        ListingTableResult built = ListingTable.Build(Palette(), Columns, [hostile], 200);
        string output = Render(built, 200);

        Assert.Contains("oktitle", output, StringComparison.Ordinal);
        Assert.DoesNotContain("]52;", output, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', output);
        Assert.DoesNotContain('\u009b', output);
    }

    [Theory]
    [InlineData(80, new[] { "Campaign", "Updated" })]
    [InlineData(100, new[] { "Campaign" })]
    [InlineData(140, new string[0])]
    public void Optional_columns_are_left_out_least_valuable_first_until_the_rest_fit(int width, string[] expectedHidden)
    {
        ListingTableResult built = ListingTable.Build(Palette(), Columns, [Wide], width);

        Assert.Equal(expectedHidden, built.HiddenHeaders);

        string output = Render(built, width);

        // The identifier is whole, on one line and unmarked by an ellipsis, however narrow the terminal.
        string idLine = Assert.Single(output.ReplaceLineEndings("\n").Split('\n'), line => line.Contains(Id, StringComparison.Ordinal));

        Assert.DoesNotContain("…", idLine, StringComparison.Ordinal);

        Assert.DoesNotContain("…", output, StringComparison.Ordinal);

        Assert.All(
            output.ReplaceLineEndings("\n").Split('\n'),
            line => Assert.True(line.Length <= width, $"{line.Length} columns overflow {width}: {line}"));
    }

    [Fact]
    public void The_flexible_column_keeps_words_whole_at_80_columns()
    {
        ListingTableResult built = ListingTable.Build(Palette(), Columns, [Wide], 80);

        string output = Render(built, 80);

        foreach (string word in new[] { "Refactor", "nightly", "import", "failed", "whole", "batch" })
        {
            Assert.Contains(word, output, StringComparison.Ordinal);
        }

        Assert.Contains("Goal", output, StringComparison.Ordinal);

        Assert.Contains("Status", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_listing_is_not_narrowed_when_everything_already_fits()
    {
        Row small = new("a1", "Do it", "Idle", "c1", "today");

        ListingTableResult built = ListingTable.Build(Palette(), Columns, [small], 80);

        Assert.Empty(built.HiddenHeaders);

        string output = Render(built, 80);

        Assert.Contains("Campaign", output, StringComparison.Ordinal);

        Assert.Contains("Updated", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixed_columns_are_never_dropped_however_narrow_the_terminal()
    {
        ListingTableResult built = ListingTable.Build(Palette(), Columns, [Wide], 10);

        Assert.Equal(["Campaign", "Updated"], built.HiddenHeaders);

        Assert.Contains(Id, Render(built, 200), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_listing_still_builds_its_headings()
    {
        ListingTableResult built = ListingTable.Build(Palette(), Columns, [], 80);

        Assert.Empty(built.HiddenHeaders);

        Assert.Contains("ID", Render(built, 80), StringComparison.Ordinal);
    }
}
