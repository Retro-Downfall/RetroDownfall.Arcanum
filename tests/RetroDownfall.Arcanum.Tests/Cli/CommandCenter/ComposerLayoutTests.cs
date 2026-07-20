using Terminal.Gui.Views;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

public sealed class ComposerTextViewConfigTests
{
    [Fact]
    public void ConfigureComposerTextView_keeps_multiline_and_wordwrap()
    {
#pragma warning disable CS0618
        using TextView input = new();
#pragma warning restore CS0618
        // Reproduce the broken pattern: EnterKeyAddsLine=false kills wrap.
        input.Multiline = true;
        input.WordWrap = true;
        input.EnterKeyAddsLine = false;
        Assert.False(input.WordWrap);
        Assert.False(input.Multiline);

        CommandCenterWindow.ConfigureComposerTextView(input);
        Assert.True(input.Multiline);
        Assert.True(input.WordWrap);
        Assert.True(input.EnterKeyAddsLine);
    }
}


public sealed class ComposerLayoutTests
{
    [Fact]
    public void Empty_text_is_one_content_row()
    {
        ComposerLayoutResult result = ComposerLayout.Measure(
            string.Empty,
            viewportWidth: 40,
            terminalRows: 24,
            headerHeight: 3,
            footerHeight: 1);

        Assert.Equal(1, result.ContentRows);
        Assert.Equal(1 + ComposerLayout.BorderOverhead, result.InputHeight);
        Assert.False(result.ReserveScrollbar);
    }

    [Fact]
    public void Soft_wrap_grows_with_long_line()
    {
        string text = new('x', 80);
        ComposerLayoutResult result = ComposerLayout.Measure(
            text,
            viewportWidth: 20,
            terminalRows: 40,
            headerHeight: 3,
            footerHeight: 1);

        Assert.True(result.ContentRows >= 4);
        Assert.True(result.ContentRows <= ComposerLayout.MaxContentRows);
    }

    [Fact]
    public void Hard_newlines_count_as_rows()
    {
        Assert.Equal(3, ComposerLayout.CountWrappedRows("a\nb\nc", 40));
        Assert.Equal(4, ComposerLayout.CountWrappedRows("a\n\nb\n", 40));
    }

    [Fact]
    public void Crlf_counts_as_single_hard_break()
    {
        Assert.Equal(2, ComposerLayout.CountWrappedRows("a\r\nb", 40));
    }

    [Fact]
    public void Wide_cjk_uses_cell_width_not_string_length()
    {
        // Four fullwidth chars at width 4 → 2 rows (2 cells each → 2 per row).
        string cjk = "你好世界";
        Assert.Equal(2, ComposerLayout.CountWrappedRows(cjk, 4));
        // string.Length would wrongly suggest 1 row at width 4.
        Assert.True(cjk.Length <= 4);
    }

    [Fact]
    public void Clamp_at_ten_then_reserve_scrollbar()
    {
        string text = string.Join('\n', Enumerable.Range(0, 20).Select(i => $"line {i}"));
        ComposerLayoutResult result = ComposerLayout.Measure(
            text,
            viewportWidth: 40,
            terminalRows: 40,
            headerHeight: 3,
            footerHeight: 1);

        Assert.Equal(ComposerLayout.MaxContentRows, result.ContentRows);
        Assert.True(result.ReserveScrollbar);
    }

    [Fact]
    public void Effective_max_preserves_min_body_at_80x12()
    {
        int effective = ComposerLayout.EffectiveMaxContentRows(
            terminalRows: 12,
            headerHeight: 3,
            footerHeight: 1);

        Assert.True(effective >= ComposerLayout.MinContentRows);
        Assert.True(effective <= ComposerLayout.MaxContentRows);
        // 12 - 3 - 1 - MinBody(3) - Border(2) = 3
        Assert.Equal(3, effective);

        ComposerLayoutResult result = ComposerLayout.Measure(
            string.Join('\n', Enumerable.Range(0, 20).Select(i => $"l{i}")),
            viewportWidth: 76,
            terminalRows: 12,
            headerHeight: 3,
            footerHeight: 1);

        Assert.Equal(3, result.ContentRows);
        Assert.Equal(3 + ComposerLayout.BorderOverhead, result.InputHeight);
    }

    [Fact]
    public void Tab_expands_to_tab_stop()
    {
        // Tab at column 0 expands to 8 cells → one row at width 8.
        Assert.Equal(1, ComposerLayout.CountWrappedRows("\t", 8));
        Assert.Equal(2, ComposerLayout.CountWrappedRows("\t", 4));
    }

    [Fact]
    public void Double_trailing_newline_counts_three_rows()
    {
        Assert.Equal(2, ComposerLayout.CountWrappedRows("hello\n", 40));
        Assert.Equal(3, ComposerLayout.CountWrappedRows("hello\n\n", 40));
    }

    [Fact]
    public void Measure_grows_for_second_newline()
    {
        ComposerLayoutResult one = ComposerLayout.Measure(
            "hello\n",
            viewportWidth: 40,
            terminalRows: 40,
            headerHeight: 3,
            footerHeight: 1);
        ComposerLayoutResult two = ComposerLayout.Measure(
            "hello\n\n",
            viewportWidth: 40,
            terminalRows: 40,
            headerHeight: 3,
            footerHeight: 1);

        Assert.Equal(2, one.ContentRows);
        Assert.Equal(3, two.ContentRows);
        Assert.False(two.ReserveScrollbar);
    }
}

public sealed class CommandCenterSubmitTextTests
{
    [Fact]
    public void Chat_payload_preserves_embedded_blank_lines_exactly()
    {
        const string text = "hello\n\nworld\n\n";
        Assert.True(CommandCenterSubmitText.TryPrepare(text, out string payload, out bool isSlash));
        Assert.False(isSlash);
        Assert.Equal(text, payload);
    }

    [Fact]
    public void Whitespace_only_is_rejected()
    {
        Assert.False(CommandCenterSubmitText.TryPrepare("  \n  ", out _, out _));
        Assert.False(CommandCenterSubmitText.TryPrepare(null, out _, out _));
    }

    [Fact]
    public void Slash_payload_is_trimmed_for_parser()
    {
        Assert.True(CommandCenterSubmitText.TryPrepare("  /help  ", out string payload, out bool isSlash));
        Assert.True(isSlash);
        Assert.Equal("/help", payload);
    }

    [Fact]
    public void Turn_attachment_builder_preserves_embedded_blank_lines()
    {
        const string prompt = "line one\n\nline three";
        TurnAttachmentBuildResult result = CommandCenterTurnAttachmentBuilder.Build(
            prompt,
            workingDirectory: Path.GetTempPath(),
            preStagedPaths: [],
            settings: new ArcanumSettings());

        Assert.Equal(prompt, result.Prompt);
    }
}
