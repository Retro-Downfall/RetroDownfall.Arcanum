using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class AskCommandBuildPromptTests
{

    [Fact]
    public void BuildPrompt_joins_prompt_words()
    {

        string prompt = AskCommand.BuildPrompt(["What", "time", "is", "it?"]);

        Assert.Equal("What time is it?", prompt);

    }

    /// <summary>
    /// Tokens escaped after <c>--</c> are absorbed by the variadic prompt argument, so they reach
    /// here as ordinary prompt words rather than through a second channel.
    /// </summary>
    [Fact]
    public void BuildPrompt_joins_tokens_escaped_after_the_delimiter()
    {

        string prompt = AskCommand.BuildPrompt(["local", "time", "now"]);

        Assert.Equal("local time now", prompt);

    }

    [Fact]
    public void BuildPrompt_skips_whitespace_only_tokens()
    {

        string prompt = AskCommand.BuildPrompt(["  hello  ", "", "   ", " ", "world"]);

        Assert.Equal("hello world", prompt);

    }

    [Fact]
    public void BuildPrompt_returns_empty_when_no_tokens()
    {

        string prompt = AskCommand.BuildPrompt([]);

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

    [Fact]

    public void CreateStderrConsole_DisablesAnsiWhenInvocationColorIsDisabled()
    {

        StringWriter output = new();

        IAnsiConsole console = AskCommand.CreateStderrConsole(
            output,
            colorEnabled: false,
            interactive: false);

        console.MarkupLine("[red]diagnostic[/]");

        Assert.Equal("diagnostic" + System.Environment.NewLine, output.ToString());

    }

}
