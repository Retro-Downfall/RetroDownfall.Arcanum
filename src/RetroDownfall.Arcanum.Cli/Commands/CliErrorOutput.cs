using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Themed command diagnostics on the stream the output contract reserves for them.
/// </summary>
/// <remarks>
/// The global <see cref="AnsiConsole"/> is built over <see cref="Console.Out"/>, so a command that
/// renders a failure through it writes the failure onto the payload stream: redirecting stdout to a
/// data file captures the diagnostic instead of the data and leaves stderr empty, and under
/// <c>--output-format json</c> the text is wrapped as the document's own payload. Routing through a
/// stderr-bound console keeps the theme and the markup while restoring the split.
///
/// <para>The console is created per write rather than cached because <see cref="Console.Error"/> is
/// replaced during a run — by the test harness and by the JSON capture — and a cached writer would
/// keep publishing to a stream that is no longer current. This mirrors
/// <c>TheForgeExecuteRendering</c>, which already renders its stderr half this way.</para>
/// </remarks>
internal static class CliErrorOutput
{

    internal static void WriteMarkupLine(string markup) =>
        AnsiConsole
            .Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) })
            .MarkupLine(markup);

}
