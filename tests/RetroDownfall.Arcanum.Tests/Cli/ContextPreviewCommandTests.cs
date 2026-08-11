using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Cli.Infrastructure;

namespace RetroDownfall.Arcanum.Tests.Cli;

[Collection("GlobalConsole")]

public sealed class ContextPreviewCommandTests
{

    [Theory]

    [InlineData("context", "inspect")]

    [InlineData("context", "tools")]

    [InlineData("context", "sources")]

    [InlineData("context", "cost")]

    public async Task Preview_commands_are_discoverable(string command, string? subcommand = null)

    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        List<string> args = [command];

        if (subcommand is not null)

        {

            args.Add(subcommand);

        }

        args.Add("--help");

        CliTestResult result = await CliTestHarness.RunAsync(services, [.. args]);

        Assert.Equal(0, result.ExitCode);

        Assert.Contains("--show-content", result.Output, StringComparison.Ordinal);

        Assert.Contains("--no-retrieval", result.Output, StringComparison.Ordinal);

    }

    /// <summary>

    /// The preview verbs share <c>run</c>'s <c>ZeroOrMore</c> prompt positional, so a mistyped flag

    /// was bound as prompt text and the preview ran against a silently different request than the

    /// operator asked for. A dash-led token before the <c>--</c> terminator is a command-line

    /// error here too.

    /// </summary>

    [Theory]

    [InlineData("inspect")]

    [InlineData("tools")]

    [InlineData("sources")]

    [InlineData("cost")]

    public async Task Preview_commands_refuse_a_mistyped_option(string verb)

    {

        ServiceCollection services = new();

        CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());

        CliTestResult result = await CliTestHarness.RunAsync(
            services,
            ["--print", "context", verb, "--show-contents", "explain"]);

        Assert.Equal((int)CliExitCode.ConfigurationError, result.ExitCode);

        Assert.Contains("--show-contents", result.Error, StringComparison.Ordinal);

        Assert.Contains("--show-content", result.Error, StringComparison.Ordinal);

    }

}
