using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.InstallationReset;
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

        return await RunBeforeConfigurationAsync(
            args,
            () => RunConfiguredAsync(args)).ConfigureAwait(false);

    }

    internal static async Task<int> RunBeforeConfigurationAsync(
        string[] args,
        Func<Task<int>> continuation,
        IInstallationStartupProbe? startupProbe = null)
    {

        ArgumentNullException.ThrowIfNull(args);

        ArgumentNullException.ThrowIfNull(continuation);

        InstallationFactoryResetPreflightResult preflight =
            InstallationFactoryResetArgvPreflight.Parse(args);

        if (preflight.IsFactoryReset
            && !preflight.IsValid)
        {

            Console.Error.WriteLine(preflight.Error);

            return (int)CliExitCode.ConfigurationError;

        }

        if (IsHelpOrVersionRequest(args))
        {

            return await continuation().ConfigureAwait(false);

        }

        string? command = InstallationFactoryResetArgvPreflight.FindRootCommand(args);

        bool guardedCommand = string.Equals(command, "run", StringComparison.Ordinal)
            || string.Equals(command, "serve", StringComparison.Ordinal);

        bool resetResume = preflight.IsFactoryReset && preflight.Apply;

        if (!guardedCommand && !resetResume)
        {

            return await continuation().ConfigureAwait(false);

        }

        startupProbe ??= InstallationStartupProbe.CreateDefault();

        Result<ActiveInstallationReset?> activeRead = await startupProbe
            .ReadActiveResetAsync(CancellationToken.None)
            .ConfigureAwait(false);

        if (activeRead.IsFailure)
        {

            Console.Error.WriteLine(activeRead.Error.Message);

            return (int)CliExitCode.GenericError;

        }

        ActiveInstallationReset? active = activeRead.Value;

        if (active is null
            || (resetResume && preflight.Scope == active.Scope))
        {

            return await continuation().ConfigureAwait(false);

        }

        Console.Error.WriteLine(
            "An installation factory reset is active. Resume it with "
            + $"'arcanum data factory-reset {FormatScope(active.Scope)} --apply'.");

        return (int)CliExitCode.GenericError;

    }

    private static bool IsHelpOrVersionRequest(string[] args)
    {

        foreach (string argument in args)
        {

            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {

                break;

            }

            if (argument is "--help" or "-h" or "-?" or "/?" or "--version")
            {

                return true;

            }

        }

        return string.Equals(
            InstallationFactoryResetArgvPreflight.FindRootCommand(args),
            "help",
            StringComparison.Ordinal);

    }

    private static string FormatScope(InstallationResetScope scope) =>
        scope switch
        {
            InstallationResetScope.Workspace => "--workspace",
            InstallationResetScope.Global => "--global",
            _ => "--all",
        };

    private static async Task<int> RunConfiguredAsync(string[] args)
    {

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

        InstallationFactoryResetPreflightResult reset =
            InstallationFactoryResetArgvPreflight.Parse(args);

        if (reset.IsFactoryReset && reset.IsValid)
        {

            return true;

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
