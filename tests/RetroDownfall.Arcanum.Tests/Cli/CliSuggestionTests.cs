using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Infrastructure.Surface;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// A mistyped or removed command must say what to type instead. Arcanum ships no alias layer, so
/// the suggestion is the entire migration path for anyone with old muscle memory.
/// </summary>
[Collection("GlobalConsole")]
public sealed class CliSuggestionTests
{

    [Theory]
    [InlineData("ask", "arcanum run")]
    [InlineData("chat", "arcanum")]
    [InlineData("mana", "arcanum context cost")]
    public void Removed_top_level_commands_name_their_replacement(string removed, string replacement)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), removed, "hello");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(replacement, result.Error, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("session", "get", "session show")]
    [InlineData("session", "watch", "watch session")]
    [InlineData("apprentice", "chronicle", "watch apprentice")]
    [InlineData("campaign", "use", "use campaign")]
    [InlineData("workspace", "get", "workspace show")]
    [InlineData("mcp", "get", "mcp show")]
    public void Removed_subcommands_name_their_replacement(
        string family,
        string removed,
        string replacement)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), family, removed);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(replacement, result.Error, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData("doctro", "doctor")]
    [InlineData("sessoin", "session")]
    [InlineData("wathc", "watch")]
    public void Mistyped_commands_suggest_the_nearest_command(string typo, string expected)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), typo);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("Did you mean", result.Error, StringComparison.Ordinal);

        Assert.Contains(expected, result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// The mistyped verb is almost never the last word typed — `arcanum campain list` is the shape
    /// operators actually produce. Suggesting against the final token asked whether `list` was a
    /// root command, found nothing, and dropped the whole invocation into System.CommandLine's
    /// help dump. The failing token is the first one the parser could not match, wherever it sits.
    /// </summary>
    [Theory]
    [InlineData(new[] { "campain", "list" }, "campaign")]
    [InlineData(new[] { "sesion", "show" }, "session")]
    [InlineData(new[] { "workspac", "list" }, "workspace")]
    [InlineData(new[] { "spel", "list" }, "spell")]
    [InlineData(new[] { "campain", "show", "abc" }, "campaign")]
    public void A_mistyped_command_is_named_even_when_more_words_follow(
        string[] arguments,
        string expected)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), arguments);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains($"Did you mean `arcanum {expected}`", result.Error, StringComparison.Ordinal);

        // The suggestion must still be the whole answer, not a preface to the default rendering.
        Assert.DoesNotContain(
            "Unrecognized command or argument",
            result.Output + result.Error,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// Global options are accepted before the verb. Scanning argv for the command path stopped at
    /// the first dash, so `arcanum --json campain` reported only "the command line is invalid" and
    /// never named the command the operator meant.
    /// </summary>
    [Theory]
    [InlineData("--plain")]
    [InlineData("--json")]
    [InlineData("-v")]
    public void A_global_option_before_the_verb_does_not_suppress_the_suggestion(string option)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), option, "campain", "list");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains(
            "Did you mean `arcanum campaign`",
            result.Output + result.Error,
            StringComparison.Ordinal);

    }

    /// <summary>
    /// A removed spelling must be recognized by the path the operator typed, not by position, so a
    /// leading global option cannot hide it either.
    /// </summary>
    [Fact]
    public void A_removed_spelling_is_recognized_behind_a_global_option()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "--plain", "session", "get");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("arcanum session show", result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// A parse that failed for a reason other than an unknown verb — a missing argument, a value
    /// outside a closed set — must keep System.CommandLine's own message, which names the argument
    /// or enumerates every legal value instead of offering one guess.
    /// </summary>
    [Theory]
    [InlineData("key", "provider", "set")]
    [InlineData("help", "sessons")]
    [InlineData("completion", "bogus")]
    public void A_failure_that_is_not_a_misspelled_command_gets_no_suggestion(
        params string[] arguments)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), arguments);

        Assert.DoesNotContain("Did you mean", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void A_suggestion_is_never_executed_automatically()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "doctro");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        // Running the suggestion would print a diagnostics report; only the suggestion may appear.
        Assert.DoesNotContain("Healthy", result.Output, StringComparison.Ordinal);

        Assert.DoesNotContain("Healthy", result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// The actionable line must be the whole answer. System.CommandLine would otherwise follow it
    /// with a full help dump and its own nearest-name guess, which for a removed spelling actively
    /// contradicts the correct advice — `mana` draws "did you mean saga, data" when the real
    /// replacement is `context cost`.
    /// </summary>
    [Fact]
    public void A_suggestion_replaces_the_default_error_rendering_rather_than_preceding_it()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "mana");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("arcanum context cost", result.Error, StringComparison.Ordinal);

        string combined = result.Output + result.Error;

        Assert.DoesNotContain("was not matched", combined, StringComparison.Ordinal);

        Assert.DoesNotContain("Required command was not provided", combined, StringComparison.Ordinal);

        Assert.DoesNotContain("Unrecognized command or argument", combined, StringComparison.Ordinal);

        // A help dump would list every top-level family.
        Assert.DoesNotContain("apprentice", combined, StringComparison.Ordinal);

    }

    /// <summary>
    /// A command with a required argument still parses with errors under <c>--help</c>. Suggesting
    /// a spelling there would replace the help the operator explicitly asked for, and would name a
    /// command they had already typed correctly.
    /// </summary>
    [Theory]
    [InlineData("key", "provider", "set")]
    [InlineData("completion", "install")]
    [InlineData("trial", "run")]
    [InlineData("lore", "set")]
    public void Help_on_a_command_with_required_arguments_still_shows_help(params string[] path)
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), [.. path, "--help"]);

        Assert.Equal((int)CliExitCode.Success, result.ExitCode);

        Assert.DoesNotContain("Did you mean", result.Error, StringComparison.Ordinal);

        Assert.Contains("Usage:", result.Output, StringComparison.Ordinal);

    }

    /// <summary>
    /// The nearest-match branch must never echo the token the operator typed.
    /// </summary>
    [Fact]
    public void A_valid_subcommand_is_never_suggested_back_to_the_caller()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "key", "provider", "set");

        Assert.DoesNotContain("Did you mean `arcanum key provider set`", result.Error, StringComparison.Ordinal);

    }

    [Fact]
    public void An_unrelated_word_gets_no_invented_suggestion()
    {

        CliTestResult result = CliTestHarness.Run(CreateServices(), "zzzzzzzzzzzzzz");

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.DoesNotContain("Did you mean", result.Error, StringComparison.Ordinal);

    }

    /// <summary>
    /// Documented examples are executable claims. Every one must still parse against the live tree,
    /// so a renamed verb or removed option breaks the build instead of shipping stale help.
    /// </summary>
    [Fact]
    public void Every_documented_example_parses_against_the_live_tree()
    {

        ServiceCollection services = CreateServices();

        using ServiceProvider provider = services.BuildServiceProvider();

        RootCommand root = CliCommandTree.Build(provider, out _);

        List<string> broken = [];

        foreach (CliSurfaceCommand command in CliSurfaceTests.Walk(CliSurfaceBuilder.Build(root)))
        {

            foreach (string example in command.Examples)
            {

                string[] tokens = Tokenize(example);

                // Must match production exactly: RunAsync disables response-file expansion, so a
                // documented `@path` value is application syntax rather than a token replacer.
                // Parsing with the default configuration would report a missing response file for
                // every legitimate `--plan @plan.json` example.
                ParseResult parsed = root.Parse(
                    tokens,
                    new ParserConfiguration { ResponseFileTokenReplacer = null });

                if (parsed.Errors.Count > 0)
                {

                    broken.Add($"{example} -> {parsed.Errors[0].Message}");

                }

            }

        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));

    }

    /// <summary>
    /// Every runnable command either carries an example or records why it cannot safely have one.
    /// Silence is not an acceptable third state.
    /// </summary>
    [Fact]
    public void Every_runnable_command_has_an_example_or_a_recorded_reason()
    {

        List<string> undocumented =
        [
            .. CliSurfaceTests
                .Walk(CliSurfaceTests.BuildMap())
                .Where(static command =>
                    command.Runnable
                    && command.Examples.Count == 0
                    && CliSurfaceExamples.ExemptionReason(command.Path) is null)
                .Select(static command => command.Path),
        ];

        Assert.Empty(undocumented);

    }

    [Fact]
    public void Every_recorded_exemption_names_a_command_that_exists()
    {

        HashSet<string> paths =
        [
            .. CliSurfaceTests.Walk(CliSurfaceTests.BuildMap()).Select(static command => command.Path),
        ];

        List<string> stale =
        [
            .. CliSurfaceExamples.ExemptPaths.Where(path => !paths.Contains(path)),
        ];

        Assert.Empty(stale);

    }

    [Fact]
    public void Every_example_path_names_a_command_that_exists()
    {

        HashSet<string> paths =
        [
            .. CliSurfaceTests.Walk(CliSurfaceTests.BuildMap()).Select(static command => command.Path),
        ];

        List<string> stale =
        [
            .. CliSurfaceExamples.Paths.Where(path => !paths.Contains(path)),
        ];

        Assert.Empty(stale);

    }

    /// <summary>
    /// Splits an example the way a shell would for the simple quoting the examples use. Examples
    /// containing a pipe are split at the pipe and only the arcanum side is parsed.
    /// </summary>
    private static string[] Tokenize(string example)
    {

        string command = example[(example.LastIndexOf('|') + 1)..].Trim();

        command = command["arcanum".Length..].TrimStart();

        List<string> tokens = [];

        System.Text.StringBuilder current = new();

        bool quoted = false;

        foreach (char character in command)
        {

            if (character == '"')
            {

                quoted = !quoted;

                continue;

            }

            if (character == ' ' && !quoted)
            {

                if (current.Length > 0)
                {

                    tokens.Add(current.ToString());

                    current.Clear();

                }

                continue;

            }

            current.Append(character);

        }

        if (current.Length > 0)
        {

            tokens.Add(current.ToString());

        }

        return [.. tokens];

    }

    private static ServiceCollection CreateServices()
    {

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        return services;

    }

}
