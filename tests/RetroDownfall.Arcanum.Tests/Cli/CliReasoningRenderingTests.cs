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

    private static ConfiguredThemePalette CreateTheme()
    {
        ThemeSemanticColors colors = new();
        return new ConfiguredThemePalette(colors, colors);
    }
}
