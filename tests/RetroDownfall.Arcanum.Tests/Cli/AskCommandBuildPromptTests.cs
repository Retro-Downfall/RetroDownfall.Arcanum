using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class AskCommandBuildPromptTests
{

    [Fact]
    public void BuildPrompt_joins_prompt_words()
    {

        string prompt = AskCommand.BuildPrompt(["What", "time", "is", "it?"], escapedArguments: null);

        Assert.Equal("What time is it?", prompt);

    }

    [Fact]
    public void BuildPrompt_appends_remaining_raw_tokens_after_delimiter()
    {

        string prompt = AskCommand.BuildPrompt(["local"], escapedArguments: ["time", "now"]);

        Assert.Equal("local time now", prompt);

    }

    [Fact]
    public void BuildPrompt_skips_whitespace_only_tokens()
    {

        string prompt = AskCommand.BuildPrompt(["  hello  ", "", "   "], escapedArguments: [" ", "world"]);

        Assert.Equal("hello world", prompt);

    }

    [Fact]
    public void BuildPrompt_returns_empty_when_no_tokens()
    {

        string prompt = AskCommand.BuildPrompt([], escapedArguments: null);

        Assert.Equal(string.Empty, prompt);

    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveStreamEndedWithoutResult_DifferentiatesEmptyAndDisconnect(bool receivedAnyStreamEvent)
    {

        string message = AskCommand.ResolveStreamEndedWithoutResult(receivedAnyStreamEvent);

        Assert.Contains(ArcanumApiClient.StreamDoctorHint, message, StringComparison.Ordinal);

        if (receivedAnyStreamEvent)
        {

            Assert.Contains(ArcanumApiClient.StreamDisconnectMessage, message, StringComparison.Ordinal);

        }
        else
        {

            Assert.Contains(ArcanumApiClient.StreamEmptyResultMessage, message, StringComparison.Ordinal);

            Assert.Contains(ArcanumApiClient.StreamUnreachableMessage, message, StringComparison.Ordinal);

        }

    }

    [Fact]
    public void FormatStreamTransportError_AppendsDoctorHintForKnownTransportCopy()
    {

        string formatted = AskCommand.FormatStreamTransportError(ArcanumApiClient.StreamTimeoutMessage);

        Assert.Equal($"{ArcanumApiClient.StreamTimeoutMessage} {ArcanumApiClient.StreamDoctorHint}", formatted);

    }

}
