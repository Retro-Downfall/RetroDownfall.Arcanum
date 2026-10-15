using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// An opt-out from ANSI has to survive the environment it runs in. Spectre applies its profile
/// enrichers after <see cref="AnsiConsoleSettings.Ansi"/>, and the GitHub Actions enricher forces
/// ANSI on, so on a runner that sets <c>GITHUB_ACTIONS=true</c> an explicit
/// <see cref="AnsiSupport.No"/> was silently undone: <c>--plain</c>, <c>--json</c>,
/// <c>NO_COLOR</c> and a redirected stdout all produced escape sequences.
/// </summary>
/// <remarks>
/// The detector is supplied through <see cref="AnsiConsoleSettings.EnvironmentVariables"/> rather
/// than the real process environment, so these assertions hold the same on a developer machine as on
/// the runner they are about.
/// </remarks>
public sealed class CliConsoleFactoryTests
{

    private const string BoldMarkup = "[bold]plain words[/]";

    /// <summary>
    /// The control. Without it a passing factory assertion could mean the detector never ran.
    /// </summary>
    [Fact]
    public void Spectre_alone_lets_the_GitHub_enricher_override_an_explicit_ansi_opt_out()
    {

        StringWriter output = new();

        IAnsiConsole console = AnsiConsole.Create(DeclinedAnsiInsideGitHubActions(output));

        Assert.True(console.Profile.Capabilities.Ansi);

        console.MarkupLine(BoldMarkup);

        Assert.Contains('\u001b', output.ToString());

    }

    [Fact]
    public void A_console_that_declines_ansi_stays_free_of_it_inside_GitHub_Actions()
    {

        StringWriter output = new();

        IAnsiConsole console = CliConsoleFactory.Create(DeclinedAnsiInsideGitHubActions(output));

        Assert.False(console.Profile.Capabilities.Ansi);

        console.MarkupLine(BoldMarkup);

        Assert.DoesNotContain('\u001b', output.ToString());

        Assert.Equal("plain words" + System.Environment.NewLine, output.ToString());

    }

    [Theory]
    [InlineData(AnsiSupport.Detect)]
    [InlineData(AnsiSupport.Yes)]
    public void A_console_that_has_not_declined_ansi_keeps_the_environment_enrichers(AnsiSupport ansi)
    {

        AnsiConsoleSettings settings = DeclinedAnsiInsideGitHubActions(new StringWriter());

        settings.Ansi = ansi;

        IAnsiConsole console = CliConsoleFactory.Create(settings);

        Assert.True(settings.Enrichment.UseDefaultEnrichers);

        Assert.True(console.Profile.Capabilities.Ansi);

    }

    [Fact]
    public void A_console_that_declines_ansi_has_no_default_enrichers()
    {

        AnsiConsoleSettings settings = DeclinedAnsiInsideGitHubActions(new StringWriter());

        _ = CliConsoleFactory.Create(settings);

        Assert.False(settings.Enrichment.UseDefaultEnrichers);

    }

    /// <summary>
    /// Built here, not through <c>CliTestHarness</c>: the harness pre-installs a console that already
    /// declines ANSI, so a harness-level assertion could not tell an invocation that replaced the
    /// stdout console from one that left it alone.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Each_per_invocation_opt_out_builds_a_stdout_console_without_ansi_colour_or_interaction(
        bool plain,
        bool json,
        bool print)
    {

        CliInvocationOptions options = new(Json: json, Plain: plain, Yes: false, Print: print);

        StringWriter output = new();

        AnsiConsoleSettings? settings = CliApplicationFactory.CreateInvocationConsoleSettings(options, output);

        Assert.NotNull(settings);

        Assert.Equal(AnsiSupport.No, settings.Ansi);

        Assert.Equal(ColorSystemSupport.NoColors, settings.ColorSystem);

        Assert.Equal(InteractionSupport.No, settings.Interactive);

        settings.EnvironmentVariables = new Dictionary<string, string>
        {

            ["GITHUB_ACTIONS"] = "true",

        };

        IAnsiConsole console = CliConsoleFactory.Create(settings);

        Assert.False(settings.Enrichment.UseDefaultEnrichers);

        Assert.False(console.Profile.Capabilities.Ansi);

        Assert.Equal(ColorSystem.NoColors, console.Profile.Capabilities.ColorSystem);

        Assert.False(console.Profile.Capabilities.Interactive);

        console.MarkupLine(BoldMarkup);

        Assert.Equal("plain words" + System.Environment.NewLine, output.ToString());

    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void An_invocation_with_no_opt_out_leaves_the_stdout_console_to_detection(bool yes, bool verbose)
    {

        CliInvocationOptions options = new(Json: false, Plain: false, Yes: yes, Verbose: verbose);

        Assert.Null(CliApplicationFactory.CreateInvocationConsoleSettings(options, new StringWriter()));

    }

    [Fact]
    public void Settings_are_required()
    {

        _ = Assert.Throws<ArgumentNullException>(() => CliConsoleFactory.Create(null!));

    }

    private static AnsiConsoleSettings DeclinedAnsiInsideGitHubActions(StringWriter output) =>
        new()
        {

            Ansi = AnsiSupport.No,

            ColorSystem = ColorSystemSupport.NoColors,

            Out = new AnsiConsoleOutput(output),

            EnvironmentVariables = new Dictionary<string, string>
            {

                ["GITHUB_ACTIONS"] = "true",

            },

        };

}
