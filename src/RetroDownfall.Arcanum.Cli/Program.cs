using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
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

        try
        {

            configuration.AddArcanumConfiguration();

        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {

            // The dispatcher and its DI graph do not exist yet, so this one bootstrap diagnostic goes
            // straight to stderr. Repair commands keep running on defaults so `doctor` and
            // `config validate` can name the offending pointer instead of crashing the same way.
            Console.Error.WriteLine(
                CliBootstrapDiagnostics.DescribeBootstrapFailure(
                    exception,
                    Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.json")));

            if (!CliBootstrapDiagnostics.AllowsDegradedConfiguration(args))
            {

                return (int)CliExitCode.ConfigurationError;

            }

        }

        CliApplicationFactory.ConfigureAnsiConsoleForEnvironment(configuration);

        CliApplicationFactory.ConfigureCliServices(services, configuration);

        ServiceProvider provider = services.BuildServiceProvider();

        return await CliApplicationFactory.RunAsync(args, provider).ConfigureAwait(false);

    }

}

/// <summary>
/// Decides how the CLI reacts when <c>arcanum.json</c> cannot be loaded at all. Configuration repair
/// and diagnosis verbs must survive a malformed file — they are the only way an operator can find out
/// what to fix — while every other verb aborts with a named remedy and <see cref="CliExitCode.ConfigurationError"/>.
/// </summary>
internal static class CliBootstrapDiagnostics
{

    private static readonly string[] DiagnosticVerbs = ["doctor", "config"];

    private static readonly string[] HelpOrVersionFlags = ["--help", "-h", "-?", "/?", "--version"];

    internal static bool AllowsDegradedConfiguration(string[] args)
    {

        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {

            return false;

        }

        foreach (string arg in args)
        {

            if (HelpOrVersionFlags.Contains(arg, StringComparer.Ordinal))
            {

                return true;

            }

        }

        return DiagnosticVerbs.Contains(args[0], StringComparer.Ordinal);

    }

    internal static string DescribeBootstrapFailure(Exception exception, string configurationPath)
    {

        ArgumentNullException.ThrowIfNull(exception);

        return string.Concat(
            exception.Message,
            Environment.NewLine,
            "Run 'arcanum config edit' to repair ",
            configurationPath,
            ", or 'arcanum doctor' for full diagnostics.");

    }

}
