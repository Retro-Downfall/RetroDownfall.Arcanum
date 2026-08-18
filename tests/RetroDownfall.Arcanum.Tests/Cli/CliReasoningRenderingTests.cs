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

        Assert.Equal("final answer", content.AnswerText);
        Assert.Equal("thinkmore", content.ReasoningText);
        Assert.DoesNotContain("think", content.AnswerText, StringComparison.Ordinal);
        Assert.DoesNotContain("more", content.AnswerText, StringComparison.Ordinal);
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

    [Fact]
    public void Reasoning_renderer_escapes_spectre_markup()
    {
        TestConsole console = new TestConsole().Width(100);

        console.Write(EphemeralReasoningRenderer.Build("[red]unsafe[/]", CreateTheme()));

        Assert.Contains("[red]unsafe[/]", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Streaming_render_cadence_coalesces_high_delta_chunks_and_flushes_final_partial()
    {
        long elapsedMilliseconds = 0;
        StreamingRenderCadence cadence = new(() => elapsedMilliseconds);

        for (int i = 0; i < 31; i++)
        {
            cadence.NoteChunk();
            Assert.False(cadence.ShouldRefresh(force: false));
        }

        cadence.NoteChunk();
        Assert.True(cadence.ShouldRefresh(force: false));
        cadence.MarkRefreshed();

        cadence.NoteChunk();
        Assert.False(cadence.ShouldRefresh(force: false));
        elapsedMilliseconds = 75;
        Assert.True(cadence.ShouldRefresh(force: false));
        cadence.MarkRefreshed();

        cadence.NoteChunk();
        Assert.True(cadence.ShouldRefresh(force: true));
    }

    [Fact]
    public void Stream_content_ignores_unstructured_reasoning_payload()
    {
        CliStreamContent content = new();

        Assert.False(content.AppendReasoning(new IntelligenceEvent(
            IntelligenceEventType.Reasoning,
            "not explicitly client-safe")));

        Assert.Equal(string.Empty, content.ReasoningText);
        Assert.Equal(string.Empty, content.AnswerText);
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
        Assert.Equal(string.Empty, content.AnswerText);
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
