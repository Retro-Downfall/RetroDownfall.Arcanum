using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.UX;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class TerminalTextSanitizerTests
{
    [Fact]
    public void Text_without_control_characters_keeps_its_instance()
    {
        string clean = "plain text\nwith 你好 and 🜁 and [31m brackets";

        Assert.Same(clean, TerminalTextSanitizer.SanitizeBlock(clean));
    }

    /// <summary>
    /// A single-line sink (a sidebar row, a header, a prompt) cannot take a line break, so a line is the
    /// block rules plus newlines flattened to spaces.
    /// </summary>
    [Theory]
    [InlineData("a\u001b]52;c;AAAA\u0007b", "ab")]
    [InlineData("one\r\ntwo\nthree", "one  two three")]
    [InlineData("x\u001b[31mred\u009b\u007fy", "xredy")]
    [InlineData("ab\tc", "ab      c")]
    public void A_line_is_the_block_rules_with_line_breaks_flattened(string input, string expected) =>
        Assert.Equal(expected, TerminalTextSanitizer.SanitizeLine(input));

    [Fact]
    public void A_line_without_anything_to_strip_keeps_its_instance_and_null_becomes_empty()
    {
        string clean = "plain 你好 line [31m";

        Assert.Same(clean, TerminalTextSanitizer.SanitizeLine(clean));
        Assert.Equal(string.Empty, TerminalTextSanitizer.SanitizeLine(null));
    }

    [Fact]
    public void Null_and_empty_text_become_empty()
    {
        Assert.Equal(string.Empty, TerminalTextSanitizer.SanitizeBlock(null));
        Assert.Equal(string.Empty, TerminalTextSanitizer.SanitizeBlock(string.Empty));
    }

    [Theory]
    [InlineData("\u001b[31mred\u001b[0m", "red")]
    [InlineData("a\u001b[1;5Hb", "ab")]
    [InlineData("a\u001b[?25lb", "ab")]
    [InlineData("x\u001b]0;title\u0007y", "xy")]
    [InlineData("x\u001b]52;c;AAAA\u001b\\y", "xy")]
    [InlineData("x\u001bPq#0;2;0;0;0\u001b\\y", "xy")]
    [InlineData("x\u001bcy", "xy")]
    [InlineData("x\u001b(By", "xy")]
    [InlineData("tail\u001b", "tail")]
    public void Complete_escape_sequences_are_dropped_whole(string input, string expected) =>
        Assert.Equal(expected, TerminalTextSanitizer.SanitizeBlock(input));

    [Fact]
    public void Text_that_only_looks_like_a_sequence_is_left_alone()
    {
        const string looksLikeCsi = "build [31m failed [1m] and [0;1m";

        Assert.Equal(looksLikeCsi, TerminalTextSanitizer.SanitizeBlock(looksLikeCsi));
    }

    /// <summary>
    /// An unterminated string sequence must not eat what follows it: the stream may never send the
    /// terminator, and the rest of the answer is ordinary text.
    /// </summary>
    [Fact]
    public void An_unterminated_string_sequence_stops_at_the_end_of_the_line()
    {
        string sanitized = TerminalTextSanitizer.SanitizeBlock("x\u001b]0;title\nnext line");

        Assert.DoesNotContain('\u001b', sanitized);
        Assert.EndsWith("\nnext line", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unterminated_string_sequence_is_bounded_by_length()
    {
        string tail = new string('z', 6000);

        string sanitized = TerminalTextSanitizer.SanitizeBlock("\u001b]0;" + tail);

        Assert.DoesNotContain('\u001b', sanitized);
        Assert.EndsWith(tail, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_controls_delete_and_the_eight_bit_c1_range_are_dropped_one_at_a_time()
    {
        Assert.Equal(
            "abcdefg",
            TerminalTextSanitizer.SanitizeBlock("a\u0000b\u0008c\rd\u007fe\u0085f\u009bg"));
    }

    [Fact]
    public void Newlines_are_kept_and_lone_carriage_returns_are_dropped()
    {
        Assert.Equal("one\ntwo", TerminalTextSanitizer.SanitizeBlock("one\r\ntwo"));
        Assert.Equal("onetwo", TerminalTextSanitizer.SanitizeBlock("one\rtwo"));
    }

    [Fact]
    public void Tabs_expand_to_the_next_tab_stop_and_the_column_restarts_on_a_newline()
    {
        int tabStop = TerminalCellMetrics.TabStop;

        string sanitized = TerminalTextSanitizer.SanitizeBlock("a\tb\nabc\td");

        Assert.Equal(
            "a" + new string(' ', tabStop - 1) + "b\nabc" + new string(' ', tabStop - 3) + "d",
            sanitized);
    }

    [Fact]
    public void A_tab_after_a_wide_glyph_counts_its_two_cells()
    {
        int tabStop = TerminalCellMetrics.TabStop;

        string sanitized = TerminalTextSanitizer.SanitizeBlock("你\tb");

        Assert.Equal("你" + new string(' ', tabStop - 2) + "b", sanitized);
    }
}
