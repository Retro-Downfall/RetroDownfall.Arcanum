using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;

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
/// <c>ExecuteResultRendering</c>, which already renders its stderr half this way.</para>
/// </remarks>
internal static class CliErrorOutput
{

    internal static void WriteMarkupLine(string markup) =>
        AnsiConsole
            .Create(CreateSettings(Console.Error))
            .MarkupLine(markup);

    /// <summary>
    /// Colour is decided here rather than inherited, because leaving this console on the process
    /// global was what carried <c>--plain</c>, <c>--output-format json</c>, <c>NO_COLOR</c> and
    /// <c>ARCANUM_NO_COLOR</c> to a themed error: <c>CliApplicationFactory</c> rebuilds
    /// <see cref="AnsiConsole.Console"/> without colour for each of them, and Spectre knows none of
    /// them on its own. A private console left on <see cref="AnsiSupport.Detect"/> answers from the
    /// terminal alone, so every one of those four contracts would stop applying to the ~200 error
    /// sites that render through here.
    ///
    /// <para>Only the explicit opt-outs are forced. With none of them present the capabilities stay
    /// on <c>Detect</c> so Spectre can answer for the actual stream — the right question now that
    /// the writes land on stderr, where the factory's stdout-redirection test no longer applies.
    /// <c>--print</c> joins the list because it is the headless marker
    /// <see cref="ICliEnvironment.ColorEnabled"/> already treats as a colour opt-out, and this
    /// writer must not be the one surface that disagrees.</para>
    /// </summary>
    internal static AnsiConsoleSettings CreateSettings(TextWriter output)
    {

        bool colorEnabled = ColorEnabled();

        return new AnsiConsoleSettings
        {

            Ansi = colorEnabled ? AnsiSupport.Detect : AnsiSupport.No,

            ColorSystem = colorEnabled ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,

            Out = new AnsiConsoleOutput(output),

        };

    }

    /// <summary>
    /// Read from the ambient invocation rather than from an injected <see cref="ICliEnvironment"/>
    /// because the ~200 call sites reach this writer statically; the options are the same
    /// <see cref="AsyncLocal{T}"/> record <c>ConsoleDispatcher</c> consults for its own
    /// <c>--plain</c> stripping.
    /// </summary>
    private static bool ColorEnabled()
    {

        CliInvocationOptions options = CliInvocationContext.Current;

        if (options.Plain || options.Json || options.Print)
        {

            return false;

        }

        return !CliEnvironment.NoColorRequested;

    }

}
