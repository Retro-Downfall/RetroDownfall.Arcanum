using RetroDownfall.Arcanum.Cli.Infrastructure;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.UX;

/// <summary>
/// The one place the CLI turns <see cref="AnsiConsoleSettings"/> into a console.
/// </summary>
/// <remarks>
/// Spectre applies its default profile enrichers <em>after</em> it has applied
/// <see cref="AnsiConsoleSettings.Ansi"/>, and the enricher for a CI provider — GitHub Actions
/// announces itself with <c>GITHUB_ACTIONS=true</c> — forces <c>Capabilities.Ansi</c> on, overriding
/// an explicit <see cref="AnsiSupport.No"/>. Left alone, <c>--plain</c>, <c>--json</c>,
/// <c>NO_COLOR</c>, <c>ARCANUM_NO_COLOR</c> and a redirected stdout all emit escape sequences inside
/// any job on such a runner, which is the one place the output is most likely to be captured into a
/// log or a file.
///
/// <para>An opt-out has to be a decision no detector can undo, so a console that declines ANSI is
/// built with no environment enrichers at all. A console left on <see cref="AnsiSupport.Detect"/> or
/// <see cref="AnsiSupport.Yes"/> keeps them: there the detectors are answering a question the caller
/// asked rather than overriding one it answered.</para>
///
/// <para>Every CLI console is built here rather than by calling <c>AnsiConsole.Create</c> directly,
/// and a source pin keeps it that way: a console built beside this factory would be the one surface
/// where the opt-outs stop applying on a CI runner.</para>
/// </remarks>
internal static class CliConsoleFactory
{

    /// <summary>
    /// Creates the console for <paramref name="settings"/>, withholding the environment enrichers from
    /// any console that declines ANSI.
    /// </summary>
    /// <param name="settings">
    /// The caller's settings. Taken over by this call: an ANSI opt-out switches the settings' default
    /// enrichers off, so a caller reuses a fresh instance rather than the one it passed.
    /// </param>
    internal static IAnsiConsole Create(AnsiConsoleSettings settings)
    {

        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Ansi == AnsiSupport.No)
        {

            settings.Enrichment.UseDefaultEnrichers = false;

        }

        return AnsiConsole.Create(settings);

    }

    /// <summary>
    /// A console bound to the current <see cref="Console.Error"/> under the diagnostic colour policy of
    /// <see cref="CreateDiagnosticSettings"/>: the one the themed failure writer, the tool-call summary
    /// and the <c>ask_human</c> diagnostics build through.
    /// </summary>
    /// <remarks>
    /// Created per call rather than cached, because <see cref="Console.Error"/> is replaced during a run
    /// — by the test harness and by the JSON capture — and a cached writer would keep publishing to a
    /// stream that is no longer current.
    ///
    /// <para>Every writer to standard error applies the same opt-outs, but not every one builds through
    /// this method: the credential prompt must stay interactive, so it decides them in
    /// <c>SensitiveValueInput.CreatePromptSettings</c> and builds with <see cref="Create"/>.</para>
    /// </remarks>
    internal static IAnsiConsole CreateDiagnostic() =>
        Create(CreateDiagnosticSettings(Console.Error));

    /// <summary>
    /// Colour is decided here rather than inherited, because leaving a stderr console on the process
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
    internal static AnsiConsoleSettings CreateDiagnosticSettings(TextWriter output)
    {

        bool colorEnabled = DiagnosticColorEnabled();

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
    private static bool DiagnosticColorEnabled()
    {

        CliInvocationOptions options = CliInvocationContext.Current;

        if (options.Plain || options.Json || options.Print)
        {

            return false;

        }

        return !CliEnvironment.NoColorRequested;

    }

}
