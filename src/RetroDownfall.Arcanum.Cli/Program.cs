using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Cli;

[ExcludeFromCodeCoverage] // Reason: System.CommandLine entrypoint; command wiring is covered via CliApplicationFactory and command unit tests.
internal static class Program
{

    public static async Task<int> Main(string[] args)
    {

        if (SandboxExecHelper.TryHandle(args))
        {

            return 0;

        }

        AppContext.SetSwitch("Microsoft.AspNetCore.Mvc.ApiExplorer.IsEnhancedModelMetadataSupportEnabled", false);

        ServiceCollection services = new();

        ConfigurationManager configuration = new();

        configuration.AddArcanumConfiguration();

        CliApplicationFactory.ConfigureAnsiConsoleForEnvironment(configuration);

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        return await CliApplicationFactory.RunAsync(args, provider).ConfigureAwait(false);

    }

}
