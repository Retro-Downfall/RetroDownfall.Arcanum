using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// Nearly two hundred themed error sites render through <see cref="CliErrorOutput"/>. It builds its own stderr-bound console per write, so the process-global <see cref="AnsiConsole"/> — the one <c>CliApplicationFactory</c> strips of colour for <c>--plain</c>, <c>--output-format json</c>, <c>NO_COLOR</c> and <c>ARCANUM_NO_COLOR</c> — is no longer in the path. The colour decision has to be reached here or those four contracts stop applying to every command failure.
/// </summary>
/// <remarks>
/// Asserted on the settings rather than on rendered bytes because Spectre's <see cref="AnsiSupport.Detect"/> answers from the process's own streams: under a test runner they are redirected, so a <c>Detect</c> console emits no escapes either and a byte-level assertion would pass while the contract was broken. <see cref="AnsiSupport.No"/> is the only state that holds on an operator's terminal too.
///
/// <para>The collection is <c>ProcessEnvironment</c> because the no-colour variables are read live from the process environment; it disables parallelization, which also makes the <see cref="Console.Error"/> swap in the render test safe.</para>
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class CliErrorOutputTests
{

    [Fact]
    public void Plain_forces_colour_off_on_the_diagnostic_console()
    {

        AssertColourSuppressed(new CliInvocationOptions(Json: false, Plain: true, Yes: false));

    }

    [Fact]
    public void Json_forces_colour_off_on_the_diagnostic_console()
    {

        AssertColourSuppressed(new CliInvocationOptions(Json: true, Plain: false, Yes: false));

    }

    [Fact]
    public void Print_forces_colour_off_on_the_diagnostic_console()
    {

        AssertColourSuppressed(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false, Print: true));

    }

    [Fact]
    public void No_color_forces_colour_off_on_the_diagnostic_console()
    {

        AssertColourSuppressed(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false),
            noColor: "1");

    }

    [Fact]
    public void Arcanum_no_color_forces_colour_off_on_the_diagnostic_console()
    {

        AssertColourSuppressed(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false),
            arcanumNoColor: "true");

    }

    /// <summary>
    /// The other half of the contract: with no opt-out present the decision belongs to the stream, so the settings must stay on <c>Detect</c> rather than being hard-wired off. A themed CLI that answered <c>NoColors</c> here would have lost its error colour on every terminal.
    /// </summary>
    [Fact]
    public void An_invocation_with_no_opt_out_leaves_the_decision_to_the_stream()
    {

        using EnvironmentScope environment = new(noColor: null, arcanumNoColor: null);

        using IDisposable invocation = CliInvocationContext.Push(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false));

        AnsiConsoleSettings settings = CliErrorOutput.CreateSettings(new StringWriter());

        Assert.Equal(AnsiSupport.Detect, settings.Ansi);

        Assert.Equal(ColorSystemSupport.Detect, settings.ColorSystem);

    }

    /// <summary>
    /// End of the path: the markup is rendered, the theme tags never survive as literal text, and under <c>--plain</c> nothing escape-introduced reaches the diagnostic stream.
    /// </summary>
    [Fact]
    public void Plain_writes_the_rendered_text_to_standard_error_without_escapes()
    {

        using EnvironmentScope environment = new(noColor: null, arcanumNoColor: null);

        using IDisposable invocation = CliInvocationContext.Push(
            new CliInvocationOptions(Json: false, Plain: true, Yes: false));

        TextWriter originalError = Console.Error;

        StringWriter captured = new();

        try
        {

            Console.SetError(captured);

            CliErrorOutput.WriteMarkupLine("[red]Error:[/] the vault refused the key");

        }
        finally
        {

            Console.SetError(originalError);

        }

        string written = captured.ToString();

        Assert.Contains("the vault refused the key", written, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', written);

        Assert.DoesNotContain("[red]", written, StringComparison.Ordinal);

    }

    private static void AssertColourSuppressed(
        CliInvocationOptions options,
        string? noColor = null,
        string? arcanumNoColor = null)
    {

        using EnvironmentScope environment = new(noColor, arcanumNoColor);

        using IDisposable invocation = CliInvocationContext.Push(options);

        AnsiConsoleSettings settings = CliErrorOutput.CreateSettings(new StringWriter());

        Assert.Equal(AnsiSupport.No, settings.Ansi);

        Assert.Equal(ColorSystemSupport.NoColors, settings.ColorSystem);

    }

    private sealed class EnvironmentScope : IDisposable
    {

        private readonly string? _originalNoColor = System.Environment.GetEnvironmentVariable("NO_COLOR");

        private readonly string? _originalArcanumNoColor =
            System.Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        internal EnvironmentScope(string? noColor, string? arcanumNoColor)
        {

            System.Environment.SetEnvironmentVariable("NO_COLOR", noColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", arcanumNoColor);

        }

        public void Dispose()
        {

            System.Environment.SetEnvironmentVariable("NO_COLOR", _originalNoColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", _originalArcanumNoColor);

        }

    }

}
