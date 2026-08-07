using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Core.Intelligence.Models;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class ChatCommandHelperTests
{

    [Theory]
    [InlineData("A1B2C3D4", true)]
    [InlineData("deadbeef", true)]
    [InlineData("DEADBEEF", true)]
    [InlineData("A1B2C3D", false)]
    [InlineData("A1B2C3D45", false)]
    [InlineData("G1B2C3D4", false)]
    [InlineData("1234567G", false)]
    public void IsEightCharHexDigitPrefix_validates_length_and_hex_digits(string value, bool expected)
    {

        bool actual = ChatCommand.IsEightCharHexDigitPrefix(value);

        Assert.Equal(expected, actual);

    }

    [Fact]
    public void AdvanceLineCounter_counts_explicit_newlines()
    {

        int linesPrinted = 0;

        int currentLineLen = 0;

        ChatCommand.AdvanceLineCounter("a\nb\nc", width: 80, ref linesPrinted, ref currentLineLen);

        Assert.Equal(2, linesPrinted);

        Assert.Equal(1, currentLineLen);

    }

    [Fact]
    public void AdvanceLineCounter_resets_length_on_carriage_return()
    {

        int linesPrinted = 0;

        int currentLineLen = 5;

        ChatCommand.AdvanceLineCounter("\rX", width: 80, ref linesPrinted, ref currentLineLen);

        Assert.Equal(0, linesPrinted);

        Assert.Equal(1, currentLineLen);

    }

    [Fact]
    public void AdvanceLineCounter_wraps_at_terminal_width()
    {

        int linesPrinted = 0;

        int currentLineLen = 0;

        ChatCommand.AdvanceLineCounter("1234", width: 3, ref linesPrinted, ref currentLineLen);

        Assert.Equal(1, linesPrinted);

        Assert.Equal(1, currentLineLen);

    }

    [Fact]
    public void AccumulateSessionMana_preserves_explicit_zero_round_total()
    {

        ChatCompletionUsage running = new(10, 5, 20);

        ChatCompletionUsage round = new(3, 2, 0);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.Equal(13, total.PromptTokens);

        Assert.Equal(7, total.CompletionTokens);

        Assert.Equal(20, total.TotalTokens);

    }

    [Fact]
    public void AccumulateSessionMana_uses_provider_reported_round_total_when_present()
    {

        ChatCompletionUsage running = new(1, 1, 10);

        ChatCompletionUsage round = new(2, 2, 50);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.Equal(60, total.TotalTokens);

    }

    [Fact]
    public void AccumulateSessionMana_TracksReasoningWithoutAddingItToTotal()
    {
        ChatCompletionUsage running = new(10, 5, 15, CachedTokens: 2, ReasoningTokens: 3);
        ChatCompletionUsage round = new(3, 2, 5, CachedTokens: 1, ReasoningTokens: 4);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.Equal(13, total.PromptTokens);
        Assert.Equal(7, total.CompletionTokens);
        Assert.Equal(20, total.TotalTokens);
        Assert.Equal(3, total.CachedTokens);
        Assert.Equal(7, total.ReasoningTokens);
    }

    [Fact]
    public void AccumulateSessionMana_preserves_sum_of_provider_reported_round_totals()
    {

        ChatCompletionUsage running = new(100, 100, 150);

        ChatCompletionUsage round = new(50, 50, 10);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.Equal(160, total.TotalTokens);

    }

    [Fact]
    public void AccumulateSessionMana_saturates_all_counters_without_overflow()
    {
        ChatCompletionUsage running = new(
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue,
            int.MaxValue);
        ChatCompletionUsage round = new(1, 1, 1, 1, 1);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.Equal(int.MaxValue, total.PromptTokens);
        Assert.Equal(int.MaxValue, total.CompletionTokens);
        Assert.Equal(int.MaxValue, total.TotalTokens);
        Assert.Equal(int.MaxValue, total.CachedTokens);
        Assert.Equal(int.MaxValue, total.ReasoningTokens);
    }

    [Fact]
    public void ComputeErasableRowCount_counts_the_partially_filled_final_row()
    {

        // A single-line answer occupies one terminal row even though it completed no newlines;
        // erasing zero rows left the streamed copy on screen above the rendered Markdown.
        Assert.Equal(1, ChatCommand.ComputeErasableRowCount("Hello world", width: 80));

    }

    [Fact]
    public void ComputeErasableRowCount_counts_every_occupied_row()
    {

        Assert.Equal(3, ChatCommand.ComputeErasableRowCount("a\nb\nc", width: 80));

    }

    [Fact]
    public void ComputeErasableRowCount_counts_terminal_wrap_rows()
    {

        Assert.Equal(3, ChatCommand.ComputeErasableRowCount(new string('x', 25), width: 10));

    }

    [Fact]
    public void EvaluatePromptInterrupt_first_stroke_with_text_only_clears_the_line()
    {

        ChatCommand.ReplInterruptDecision decision =
            ChatCommand.EvaluatePromptInterrupt(consecutiveInterrupts: 0, hadPendingText: true);

        Assert.False(decision.ExitRepl);

        Assert.Equal(0, decision.ConsecutiveInterrupts);

        Assert.NotNull(decision.Notice);

    }

    [Fact]
    public void EvaluatePromptInterrupt_first_stroke_on_empty_line_warns_without_exiting()
    {

        ChatCommand.ReplInterruptDecision decision =
            ChatCommand.EvaluatePromptInterrupt(consecutiveInterrupts: 0, hadPendingText: false);

        Assert.False(decision.ExitRepl);

        Assert.Equal(1, decision.ConsecutiveInterrupts);

        Assert.NotNull(decision.Notice);

    }

    [Fact]
    public void EvaluatePromptInterrupt_second_consecutive_stroke_exits_cleanly()
    {

        ChatCommand.ReplInterruptDecision decision =
            ChatCommand.EvaluatePromptInterrupt(consecutiveInterrupts: 1, hadPendingText: false);

        Assert.True(decision.ExitRepl);

    }

    [Fact]
    public void EvaluatePromptInterrupt_text_entry_resets_the_exit_countdown()
    {

        ChatCommand.ReplInterruptDecision cleared =
            ChatCommand.EvaluatePromptInterrupt(consecutiveInterrupts: 1, hadPendingText: true);

        Assert.Equal(0, cleared.ConsecutiveInterrupts);

        Assert.False(
            ChatCommand.EvaluatePromptInterrupt(
                cleared.ConsecutiveInterrupts,
                hadPendingText: false).ExitRepl);

    }

}
