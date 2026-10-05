using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using Spectre.Console.Testing;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class CliReasoningRenderingTests
{
    [Fact]
    public void Stream_content_keeps_reasoning_out_of_answer_accumulation()
    {
        CliStreamContent content = new();

        Assert.True(content.AppendReasoning(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            "think",
            Reasoning: new ReasoningContentSegment("think", ReasoningOutputMode.Summary))));
        content.AppendAnswer("final ");
        Assert.True(content.AppendReasoning(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            "more",
            Reasoning: new ReasoningContentSegment("more", ReasoningOutputMode.Summary))));
        content.AppendAnswer("answer");

        // Only the answer's own characters are counted; reasoning never adds to them.
        Assert.Equal("final answer".Length, content.AnswerLength);
        Assert.Equal("thinkmore", content.ReasoningText);
    }

    [Fact]
    public void Reasoning_renderer_creates_dimmed_labeled_ephemeral_block()
    {
        TestConsole console = new TestConsole().Width(100);
        ConfiguredThemePalette palette = CreateTheme();

        console.Write(EphemeralReasoningRenderer.Build("client-safe summary", palette));

        Assert.Contains("Reasoning (ephemeral)", console.Output, StringComparison.Ordinal);
        Assert.Contains("client-safe summary", console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reasoning text is model output, and the panel is written to a terminal: an escape sequence in it
    /// would be executed rather than shown. The panel is presentation (it renders on stderr; the answer
    /// stream on stdout stays byte-exact), so it is stripped wherever it is drawn.
    /// </summary>
    [Fact]
    public void Reasoning_panel_strips_control_characters()
    {
        TestConsole console = new TestConsole().Width(100);

        console.Write(EphemeralReasoningRenderer.Build(
            "see\u001b]52;c;AAAA\u0007 and\u001b[31m red\u009b\u007f\rdone",
            CreateTheme()));

        Assert.DoesNotContain('\u001b', console.Output);
        Assert.DoesNotContain('\u0007', console.Output);
        Assert.DoesNotContain('\u009b', console.Output);
        Assert.DoesNotContain('\u007f', console.Output);
        Assert.Contains("see and reddone", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Reasoning_renderer_escapes_spectre_markup()
    {
        TestConsole console = new TestConsole().Width(100);

        console.Write(EphemeralReasoningRenderer.Build("[red]unsafe[/]", CreateTheme()));

        Assert.Contains("[red]unsafe[/]", console.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CLI accumulated the whole answer only to learn whether its last character was a newline, so a
    /// long answer held every byte it streamed for the lifetime of the turn. The bound is on allocation:
    /// ten megabytes of answer must leave no retained copy behind.
    /// </summary>
    [Fact]
    public void Answer_accumulation_is_bounded()
    {
        CliStreamContent content = new();
        string chunk = new('a', 1024);
        const int chunkCount = 10 * 1024;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < chunkCount; i++)
        {
            content.AppendAnswer(chunk);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(10L * 1024 * 1024, content.AnswerLength);
        Assert.True(content.AnswerEndsMidLine);
        Assert.True(
            allocated < 1024 * 1024,
            $"Appending 10 MB of answer allocated {allocated:N0} bytes; the answer must not be retained.");
    }

    [Fact]
    public void The_answer_ends_mid_line_only_when_its_last_character_is_not_a_newline()
    {
        CliStreamContent content = new();

        Assert.False(content.AnswerEndsMidLine);

        content.AppendAnswer("a line\n");
        Assert.False(content.AnswerEndsMidLine);

        content.AppendAnswer("and a partial");
        Assert.True(content.AnswerEndsMidLine);

        content.NoteAnswerLineBreak();
        Assert.False(content.AnswerEndsMidLine);
    }

    [Fact]
    public void Stream_content_ignores_unstructured_reasoning_payload()
    {
        CliStreamContent content = new();

        Assert.False(content.AppendReasoning(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            "not explicitly client-safe")));

        Assert.Equal(string.Empty, content.ReasoningText);
        Assert.Equal(0, content.AnswerLength);
    }

    [Fact]
    public void Stream_content_bounds_high_delta_reasoning_with_an_explicit_marker()
    {
        CliStreamContent content = new(maxReasoningChars: 64);

        for (int i = 0; i < 1_000; i++)
        {
            Assert.True(content.AppendReasoning(new IntelligenceEvent(
                IntelligenceEventType.Reasoning,
                "abcdefghij",
                Reasoning: new ReasoningContentSegment(
                    "abcdefghij",
                    ReasoningOutputMode.Summary))));
        }

        Assert.True(content.ReasoningText.Length <= 64);
        Assert.EndsWith(CliStreamContent.ReasoningTruncationMarker, content.ReasoningText, StringComparison.Ordinal);
        Assert.Equal(0, content.AnswerLength);
    }

    /// <summary>
    /// The bound slices the incoming chunk by UTF-16 code unit. When the headroom ends between the
    /// halves of an astral character the chunk must be cut one unit short instead, or the panel
    /// renders a replacement character immediately before the truncation marker.
    /// </summary>
    [Fact]
    public void Stream_content_bounding_an_incoming_chunk_keeps_a_surrogate_pair_whole()
    {
        CliStreamContent content = new(maxReasoningChars: 64);

        AppendReasoning(content, new string('a', 39));
        AppendReasoning(content, string.Concat(Enumerable.Repeat("\U0001F642", 20)));

        AssertNoLoneSurrogate(content.ReasoningText);
        Assert.EndsWith(CliStreamContent.ReasoningTruncationMarker, content.ReasoningText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mirror cut: text already buffered past the content limit is rewound to that limit, which
    /// can strand the high surrogate of a pair that a previous append had accepted whole.
    /// </summary>
    [Fact]
    public void Stream_content_rewinding_buffered_reasoning_keeps_a_surrogate_pair_whole()
    {
        CliStreamContent content = new(maxReasoningChars: 64);

        AppendReasoning(content, new string('a', 39) + "\U0001F642");
        AppendReasoning(content, new string('b', 30));

        AssertNoLoneSurrogate(content.ReasoningText);
        Assert.EndsWith(CliStreamContent.ReasoningTruncationMarker, content.ReasoningText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The panel is a full-width box laid out from column 0. The answer stream writes raw tokens to
    /// stdout with no newline between them, so a flush that lands part-way through an answer paints
    /// the box from whatever column the last token ended on and wraps its own top border.
    /// </summary>
    [Fact]
    public void Reasoning_flush_returns_the_answer_stream_to_column_zero_first()
    {
        CliStreamContent content = new();
        content.AppendAnswer("The answer is ");
        AppendReasoning(content, "because");
        StringWriter answer = new();

        Assert.True(EphemeralReasoningRenderer.Flush(
            new TestConsole().Width(40),
            content,
            CreateTheme(),
            answer,
            answerSharesTerminal: true));

        Assert.Equal(System.Environment.NewLine, answer.ToString());
    }

    /// <summary>
    /// The break belongs to the terminal, not to the payload: a redirected stdout must receive
    /// exactly the bytes the model produced.
    /// </summary>
    [Fact]
    public void Reasoning_flush_leaves_a_redirected_answer_stream_untouched()
    {
        CliStreamContent content = new();
        content.AppendAnswer("The answer is ");
        AppendReasoning(content, "because");
        StringWriter answer = new();

        Assert.True(EphemeralReasoningRenderer.Flush(
            new TestConsole().Width(40),
            content,
            CreateTheme(),
            answer,
            answerSharesTerminal: false));

        Assert.Equal(string.Empty, answer.ToString());
    }

    [Fact]
    public void Reasoning_flush_breaks_the_answer_line_once_until_more_answer_arrives()
    {
        CliStreamContent content = new();
        content.AppendAnswer("The answer is ");
        StringWriter answer = new();

        AppendReasoning(content, "first");
        _ = Flush(content, answer);
        AppendReasoning(content, "second");
        _ = Flush(content, answer);

        Assert.Equal(System.Environment.NewLine, answer.ToString());

        content.AppendAnswer("more");
        AppendReasoning(content, "third");
        _ = Flush(content, answer);

        Assert.Equal(System.Environment.NewLine + System.Environment.NewLine, answer.ToString());
    }

    [Fact]
    public void Reasoning_flush_does_not_break_a_line_the_answer_already_ended()
    {
        CliStreamContent content = new();
        content.AppendAnswer("The answer is complete.\n");
        AppendReasoning(content, "because");
        StringWriter answer = new();

        _ = Flush(content, answer);

        Assert.Equal(string.Empty, answer.ToString());
    }

    private static bool Flush(CliStreamContent content, TextWriter answer) =>
        EphemeralReasoningRenderer.Flush(
            new TestConsole().Width(40),
            content,
            CreateTheme(),
            answer,
            answerSharesTerminal: true);

    private static void AppendReasoning(CliStreamContent content, string text) =>
        Assert.True(content.AppendReasoning(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            text,
            Reasoning: new ReasoningContentSegment(text, ReasoningOutputMode.Summary))));

    private static void AssertNoLoneSurrogate(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                Assert.True(
                    i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]),
                    $"Lone high surrogate at index {i}.");
                i++;
                continue;
            }

            Assert.False(char.IsLowSurrogate(text[i]), $"Lone low surrogate at index {i}.");
        }
    }

    private static ConfiguredThemePalette CreateTheme()
    {
        ThemeSemanticColors colors = new();
        return new ConfiguredThemePalette(colors, colors);
    }
}
