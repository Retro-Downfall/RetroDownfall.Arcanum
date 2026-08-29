using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Tests.Support;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Real-parser test harness replacing Spectre.Console.Cli.Testing's <c>CommandAppTester</c>.
/// Builds the DI container, runs the actual ConsoleAppFramework command tree via
/// <see cref="CliApplicationFactory.RunAsync"/> (the same entrypoint <c>Program.Main</c> uses),
/// captures stdout/stderr, and returns the process exit code. Callers using fakes (HTTP,
/// secret store, etc.) register them on the passed <see cref="IServiceCollection"/> before
/// calling this.
///
/// <see cref="Environment.ExitCode"/> and <see cref="Console.Out"/>/<see cref="Console.Error"/>
/// are process-global mutable state, so every test that calls this must run in the
/// <c>[Collection("GlobalConsole")]</c> collection (see GlobalConsoleCollection) to avoid racing
/// with other tests that touch the same state.
///
/// The installation startup probe is substituted here rather than left to each caller — see
/// <see cref="ApplyInstalledStartupProbe"/> for why the production one cannot answer for a test.
/// </summary>
internal static class CliTestHarness
{

    public static CliTestResult Run(IServiceCollection services, params string[] args) =>
        RunAsync(services, args).GetAwaiter().GetResult();

    public static async Task<CliTestResult> RunAsync(
        IServiceCollection services,
        string[] args,
        string? input = null)
    {

        ApplyInstalledStartupProbe(services);

        ServiceProvider provider = services.BuildServiceProvider();

        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        TextReader originalIn = Console.In;

        IAnsiConsole originalAnsiConsole = AnsiConsole.Console;

        StringWriter outWriter = new();

        StringWriter errorWriter = new();

        Console.SetOut(outWriter);

        Console.SetError(errorWriter);
        // Standard input belongs to the harness even when the test supplies none. Left alone, the
        // command reads whichever stdin the runner happened to attach: /dev/null under CI, where a
        // read returns end-of-input at once; a terminal, where `run` prompts interactively; or an
        // open pipe — how an agent or wrapper process launches `dotnet test` — where the read never
        // returns and the whole suite hangs on it forever. An empty reader is the answer CI gives,
        // given deterministically.
        Console.SetIn(input is null ? TextReader.Null : new StringReader(input));
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(outWriter),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
        });

        try
        {
            int exitCode = await CliApplicationFactory.RunAsync(args, provider).ConfigureAwait(false);

            return new CliTestResult(exitCode, outWriter.ToString(), errorWriter.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);

            Console.SetError(originalError);
            Console.SetIn(originalIn);
            AnsiConsole.Console = originalAnsiConsole;

            provider.Dispose();
        }

    }

    /// <summary>
    /// Gives the command tree an installation probe that answers for this test rather than for this
    /// machine, unless the caller registered a probe of its own.
    /// </summary>
    /// <remarks>
    /// <c>ConfigureCliServices</c> registers the production probe, which decides whether an
    /// installation exists by reading the operator's own <c>~/.config/arcanum</c> and the
    /// <c>arcanum</c> / <c>master-api-key</c> entry in the OS credential store. Neither answer is
    /// the test's: on a machine that has run <c>arcanum setup</c> the probe says "installed" and
    /// every command proceeds, while on a machine that never has — a fresh contributor checkout, or
    /// Windows CI — <c>run</c> stops at its setup gate with exit 2 and twenty-two tests about
    /// parsing, attachments and reasoning fail for a reason none of them is about. The credential
    /// does not move with a redirected <c>ARCANUM_TEST_HOME</c> either, so redirecting the home is
    /// not enough on its own. Substituting the probe here is behaviour-preserving on a machine that
    /// is set up, because that is exactly the answer the production probe gives there.
    ///
    /// A test whose subject <em>is</em> the probe — the factory-reset command tests — registers its
    /// own double, which is recognised by being a type from this assembly and left in place.
    /// </remarks>
    private static void ApplyInstalledStartupProbe(
        IServiceCollection services)
    {

        bool callerOwnsTheProbe = services.Any(static descriptor =>
            descriptor.ServiceType == typeof(IInstallationStartupProbe)
            && (descriptor.ImplementationInstance?.GetType() ?? descriptor.ImplementationType)
                ?.Assembly == typeof(CliTestHarness).Assembly);

        if (callerOwnsTheProbe)
        {

            return;

        }

        services.RemoveAll<IInstallationStartupProbe>();

        services.AddSingleton<IInstallationStartupProbe>(new InstalledStartupProbe());

    }

}

internal readonly record struct CliTestResult(int ExitCode, string Output, string Error);

/// <summary>
/// Console previews are truncated by character count. .NET strings are UTF-16, so a cutoff that
/// lands between the two halves of a surrogate pair leaves an unpaired code unit: the terminal
/// renders U+FFFD and the copied text is no longer valid Unicode. Rendered CLI output must never
/// contain one.
/// </summary>
internal static class Utf16Assert
{

    public static bool ContainsLoneSurrogate(string text)
    {

        for (int index = 0; index < text.Length; index++)
        {

            if (char.IsHighSurrogate(text[index]))
            {

                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                {
                    return true;
                }

                index++;

                continue;

            }

            if (char.IsLowSurrogate(text[index]))
            {
                return true;
            }

        }

        return false;

    }

}
