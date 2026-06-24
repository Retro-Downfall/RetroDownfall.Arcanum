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
    public void AccumulateSessionMana_sums_prompt_and_completion_tokens()
    {

        ChatCompletionUsage running = new(10, 5, 20);

        ChatCompletionUsage round = new(3, 2, 0);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.Equal(13, total.PromptTokens);

        Assert.Equal(7, total.CompletionTokens);

        Assert.Equal(25, total.TotalTokens);

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
    public void AccumulateSessionMana_never_reports_total_below_prompt_plus_completion()
    {

        ChatCompletionUsage running = new(100, 100, 150);

        ChatCompletionUsage round = new(50, 50, 10);

        ChatCompletionUsage total = ChatCommand.AccumulateSessionMana(running, round);

        Assert.True(total.TotalTokens >= total.PromptTokens + total.CompletionTokens);

        Assert.Equal(300, total.TotalTokens);

    }

}
