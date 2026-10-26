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

    [Theory]
    [InlineData("👩‍💻\tX", "👩‍💻", 2)]
    [InlineData("👨‍👩‍👧‍👦\tX", "👨‍👩‍👧‍👦", 2)]
    [InlineData("👍🏽\tX", "👍🏽", 2)]
    [InlineData("👩\u0007‍💻\tX", "👩‍💻", 2)]
    [InlineData("👩\u001b[31m‍💻\u001b[0m\tX", "👩‍💻", 2)]
    [InlineData("a\u0007\uFE0E\tX", "a\uFE0E", 1)]
    [InlineData("❤️\tX", "❤️", 1)]
    [InlineData("1️⃣\tX", "1️⃣", 1)]
    [InlineData("🜁\tX", "🜁", 1)]
    [InlineData("한\tX", "한", 2)]
    [InlineData("\u200D\tX", "\u200D", 0)]
    [InlineData("\uFE0F\tX", "\uFE0F", 0)]
    public void Tabs_count_complete_final_visible_graphemes_after_controls_are_filtered(string input, string visible, int columns)
    {
        Assert.Equal(columns, Terminal.Gui.Text.StringExtensions.GetColumns(visible));

        string expected = visible + new string(' ', TerminalCellMetrics.TabStop - columns) + "X";

        Assert.Equal(expected, TerminalTextSanitizer.SanitizeBlock(input));
        Assert.Equal(expected, TerminalTextSanitizer.SanitizeLine(input));
    }

    [Fact]
    public void Removed_carriage_returns_can_join_a_block_grapheme_but_line_breaks_still_flatten_to_spaces()
    {
        Assert.Equal("👩‍💻      X", TerminalTextSanitizer.SanitizeBlock("👩\r‍💻\tX"));
        Assert.Equal("👩 ‍💻   X", TerminalTextSanitizer.SanitizeLine("👩\r‍💻\tX"));
        Assert.Equal("👩‍💻      X\n👩‍💻      Y", TerminalTextSanitizer.SanitizeBlock("👩‍💻\tX\r\n👩‍💻\tY"));
    }

    [Fact]
    public void A_large_tabbed_payload_filters_and_expands_each_visible_cluster_without_prefix_remeasurement()
    {
        const int repetitions = 10_000;

        string input = string.Concat(Enumerable.Repeat("👩\u0007‍💻\t", repetitions));

        string expected = string.Concat(Enumerable.Repeat("👩‍💻      ", repetitions));

        Assert.Equal(expected, TerminalTextSanitizer.SanitizeBlock(input));
        Assert.Equal(expected, TerminalTextSanitizer.SanitizeLine(input));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Filtering_keeps_the_existing_replacement_policy_for_malformed_utf16(bool highSurrogate)
    {
        string input = new(['a', highSurrogate ? '\uD800' : '\uDC00', highSurrogate ? '\u0007' : '\t', 'b']);

        Assert.True(char.IsSurrogate(input[1]));

        string expected = highSurrogate ? "a�b" : "a�      b";

        Assert.Equal(expected, TerminalTextSanitizer.SanitizeBlock(input));
        Assert.Equal(expected, TerminalTextSanitizer.SanitizeLine(input));
    }
}
