using RetroDownfall.Arcanum.Cli.Commands;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Tests.Cli;

public sealed class AskCommandBuildPromptTests
{

    [Fact]
    public void BuildPrompt_joins_prompt_words()
    {

        AskCommand.Settings settings = new() { PromptWords = ["What", "time", "is", "it?"] };

        CommandContext context = CreateContext(raw: null);

        string prompt = AskCommand.BuildPrompt(settings, context);

        Assert.Equal("What time is it?", prompt);

    }

    [Fact]
    public void BuildPrompt_appends_remaining_raw_tokens_after_delimiter()
    {

        AskCommand.Settings settings = new() { PromptWords = ["local"] };

        CommandContext context = CreateContext(raw: ["time", "now"]);

        string prompt = AskCommand.BuildPrompt(settings, context);

        Assert.Equal("local time now", prompt);

    }

    [Fact]
    public void BuildPrompt_skips_whitespace_only_tokens()
    {

        AskCommand.Settings settings = new() { PromptWords = ["  hello  ", "", "   "] };

        CommandContext context = CreateContext(raw: [" ", "world"]);

        string prompt = AskCommand.BuildPrompt(settings, context);

        Assert.Equal("hello world", prompt);

    }

    [Fact]
    public void BuildPrompt_returns_empty_when_no_tokens()
    {

        AskCommand.Settings settings = new();

        CommandContext context = CreateContext(raw: null);

        string prompt = AskCommand.BuildPrompt(settings, context);

        Assert.Equal(string.Empty, prompt);

    }

    private static CommandContext CreateContext(IReadOnlyList<string>? raw)
    {

        TestRemainingArguments remaining = new(raw);

        return new CommandContext([], remaining, "ask", data: null!);

    }

    private sealed class TestRemainingArguments(IReadOnlyList<string>? raw) : IRemainingArguments
    {

        public ILookup<string, string?> Parsed { get; } =
            Array.Empty<(string Key, string? Value)>().ToLookup(x => x.Key, x => x.Value);

        public IReadOnlyList<string> Raw { get; } = raw ?? Array.Empty<string>();

    }

}
