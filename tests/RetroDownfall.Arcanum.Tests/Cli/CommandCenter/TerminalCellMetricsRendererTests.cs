using System.Globalization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using Terminal.Gui.Drivers;
using Terminal.Gui.Text;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class TerminalCellMetricsRendererTests
{
    [Theory]
    [InlineData("👩‍💻", 2)]
    [InlineData("👨‍👩‍👧‍👦", 2)]
    [InlineData("👍🏽", 2)]
    [InlineData("👩🏽‍💻", 2)]
    [InlineData("🇺🇳", 2)]
    [InlineData("🏴\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F", 2)]
    [InlineData("1️⃣", 1)]
    [InlineData("❤", 1)]
    [InlineData("❤\uFE0E", 1)]
    [InlineData("❤️", 1)]
    [InlineData("a\uFE0E", 1)]
    [InlineData("a\uFE0F", 1)]
    [InlineData(" \uFE0F", 1)]
    [InlineData("🜁", 1)]
    [InlineData("😀", 2)]
    [InlineData("界", 2)]
    [InlineData("e\u0301", 1)]
    [InlineData("한", 2)]
    public void Visible_graphemes_match_the_pinned_renderer_measurement_and_output_buffer(string grapheme, int columns)
    {
        Assert.Single(StringInfo.ParseCombiningCharacters(grapheme));
        Assert.Equal(columns, grapheme.GetColumns());

        OutputBufferImpl buffer = new();

        buffer.SetSize(40, 1);
        buffer.Move(0, 0);
        buffer.AddStr(grapheme + "X");

        Assert.Equal(columns + 1, buffer.Col);
        Assert.Equal("X", buffer.Contents![0, columns].Grapheme);
        Assert.Equal(columns, TerminalCellMetrics.MeasureWidth(grapheme));
        Assert.Equal(columns, TerminalCellMetrics.MeasureGraphemeWidth(grapheme, 0));
    }

    [Theory]
    [InlineData("\u200D")]
    [InlineData("\u200C")]
    [InlineData("\uFE00")]
    [InlineData("\uFE0E")]
    [InlineData("\uFE0F")]
    [InlineData("\u0301")]
    public void Standalone_format_and_variation_marks_do_not_manufacture_measurement_columns(string grapheme)
    {
        Assert.Equal(0, grapheme.GetColumns());
        Assert.All(grapheme.EnumerateRunes(), static rune => Assert.Equal(0, rune.GetColumns()));

        // The pinned buffer advances one cell even for a standalone zero-width cluster. Measurement
        // follows GetColumns, not that cursor quirk, and makes no universal terminal/font promise.
        OutputBufferImpl buffer = new();

        buffer.SetSize(8, 1);
        buffer.AddStr(grapheme);

        Assert.Equal(1, buffer.Col);
        Assert.Equal(0, TerminalCellMetrics.MeasureWidth(grapheme));
        Assert.Equal(0, TerminalCellMetrics.MeasureGraphemeWidth(grapheme, 3));
    }

    [Theory]
    [InlineData("👩‍💻")]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("👍🏽")]
    public void Slicing_and_wrapping_consume_whole_compound_graphemes(string grapheme)
    {
        string text = grapheme + "X";

        Assert.Equal(string.Empty, TerminalCellMetrics.TruncateToCells(text, 1));
        Assert.Equal(grapheme, TerminalCellMetrics.TruncateToCells(text, 2));
        Assert.Equal(text, TerminalCellMetrics.TruncateToCells(text, 3));
        Assert.Equal(new CellSlice(0, 0, ReachedEnd: false), TerminalCellMetrics.TakeCells(text, 0, 1, forceProgress: false));
        Assert.Equal(new CellSlice(grapheme.Length, 2, ReachedEnd: false), TerminalCellMetrics.TakeCells(text, 0, 1));

        string repeated = string.Concat(Enumerable.Repeat(grapheme, 3));

        Assert.Equal(new[] { grapheme, grapheme, grapheme }, SessionLogBuffer.WrapLine(repeated, 2));
        Assert.Equal(3, ComposerLayout.CountWrappedRows(repeated, 2));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(2, 6)]
    [InlineData(7, 1)]
    [InlineData(8, 8)]
    public void Tabs_keep_the_explicit_column_dependent_stop_outside_the_grapheme_cap(int column, int expected) =>
        Assert.Equal(expected, TerminalCellMetrics.MeasureGraphemeWidth("\t", column));

    [Fact]
    public void Mixed_text_and_zero_width_controls_follow_the_renderer_measurement()
    {
        const string text = "A👩‍💻界e\u0301🜁";

        Assert.Equal(7, text.GetColumns());
        Assert.Equal(7, TerminalCellMetrics.MeasureWidth(text));
        Assert.Equal(0, TerminalCellMetrics.MeasureWidth("\0\u0007"));
        Assert.Equal(0, TerminalCellMetrics.MeasureWidth(null));
        Assert.Equal(5, TerminalCellMetrics.MeasureWidth("plain"));
    }
}
