using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Infrastructure;
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

        ServiceProvider provider = services.BuildServiceProvider();

        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        TextReader originalIn = Console.In;

        IAnsiConsole originalAnsiConsole = AnsiConsole.Console;

        StringWriter outWriter = new();

        StringWriter errorWriter = new();

        Console.SetOut(outWriter);

        Console.SetError(errorWriter);
        if (input is not null)
        {
            Console.SetIn(new StringReader(input));
        }
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
