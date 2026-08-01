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

    [InlineData("mana", null)]

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

}
