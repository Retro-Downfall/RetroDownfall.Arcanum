using RetroDownfall.Arcanum.Cli.Commands;

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

}
