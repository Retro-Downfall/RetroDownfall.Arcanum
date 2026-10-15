using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.UX;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The hidden-credential input routes ('config set' on a sensitive key, 'key set',
/// 'key provider set') must pick between redirected stdin and the hidden prompt using the
/// invocation's headless markers, not <c>Console.IsInputRedirected</c> alone. A real terminal under
/// <c>--print</c>/<c>--output-format json</c> must behave like a redirected one — no prompt may
/// block — and <c>--plain</c> is a colour flag that must keep the documented hidden prompt alive.
/// </summary>
[Collection("GlobalConsole")]
public sealed class SensitiveValueInputTests
{
    [Fact]
    public async Task Redirected_stdin_is_read_even_when_a_headless_marker_is_present()
    {
        FakeSensitiveValueConsole console = new(isInputRedirected: true, line: "secret-value");

        SensitiveValueRead read = await SensitiveValueInput.ReadAsync(
            console,
            new CliInvocationOptions(Json: true, Plain: false, Yes: false, Print: true),
            "Sensitive value:",
            CancellationToken.None);

        Assert.True(read.IsAvailable);

        Assert.Equal("secret-value", read.Value);

        Assert.False(console.PromptWasShown);
    }

    [Fact]
    public async Task Print_on_a_terminal_refuses_instead_of_blocking_on_the_hidden_prompt()
    {
        FakeSensitiveValueConsole console = new(isInputRedirected: false, line: null);

        SensitiveValueRead read = await SensitiveValueInput.ReadAsync(
            console,
            new CliInvocationOptions(Json: false, Plain: false, Yes: false, Print: true),
            "Sensitive value:",
            CancellationToken.None);

        Assert.False(read.IsAvailable);

        Assert.False(console.PromptWasShown);
    }

    [Fact]
    public async Task Json_on_a_terminal_refuses_instead_of_blocking_on_the_hidden_prompt()
    {
        FakeSensitiveValueConsole console = new(isInputRedirected: false, line: null);

        SensitiveValueRead read = await SensitiveValueInput.ReadAsync(
            console,
            new CliInvocationOptions(Json: true, Plain: false, Yes: false),
            "Sensitive value:",
            CancellationToken.None);

        Assert.False(read.IsAvailable);

        Assert.False(console.PromptWasShown);
    }

    [Fact]
    public async Task Plain_on_a_terminal_still_reaches_the_documented_hidden_prompt()
    {
        FakeSensitiveValueConsole console = new(isInputRedirected: false, line: null)
        {
            PromptResult = "typed-secret",
        };

        SensitiveValueRead read = await SensitiveValueInput.ReadAsync(
            console,
            new CliInvocationOptions(Json: false, Plain: true, Yes: false),
            "Sensitive value:",
            CancellationToken.None);

        Assert.True(read.IsAvailable);

        Assert.Equal("typed-secret", read.Value);

        Assert.True(console.PromptWasShown);
    }

    [Fact]
    public async Task A_plain_terminal_with_no_headless_marker_prompts()
    {
        FakeSensitiveValueConsole console = new(isInputRedirected: false, line: null)
        {
            PromptResult = "typed-secret",
        };

        SensitiveValueRead read = await SensitiveValueInput.ReadAsync(
            console,
            new CliInvocationOptions(Json: false, Plain: false, Yes: false),
            "Sensitive value:",
            CancellationToken.None);

        Assert.True(read.IsAvailable);

        Assert.Equal("typed-secret", read.Value);

        Assert.True(console.PromptWasShown);
    }

    [Fact]
    public void The_prompt_console_asserts_interaction_so_plain_does_not_kill_the_hidden_prompt()
    {
        AnsiConsoleSettings plain = SensitiveValueInput.CreatePromptSettings(
            new CliInvocationOptions(Json: false, Plain: true, Yes: false));

        Assert.Equal(InteractionSupport.Yes, plain.Interactive);

        Assert.Equal(AnsiSupport.No, plain.Ansi);

        Assert.Equal(ColorSystemSupport.NoColors, plain.ColorSystem);

        // Both no-colour variables are cleared while the settings are built, because an operator's own
        // NO_COLOR would otherwise make the "styled" answer depend on the machine the suite runs on.
        AnsiConsoleSettings styled;

        using (new EnvironmentScope(gitHubActions: null, noColor: null, arcanumNoColor: null))
        {
            styled = SensitiveValueInput.CreatePromptSettings(
                new CliInvocationOptions(Json: false, Plain: false, Yes: false));
        }

        Assert.Equal(InteractionSupport.Yes, styled.Interactive);

        Assert.Equal(AnsiSupport.Detect, styled.Ansi);

        Assert.Equal(ColorSystemSupport.Detect, styled.ColorSystem);
    }

    /// <summary>
    /// Spectre's colour detector knows <c>NO_COLOR</c> but not Arcanum's own <c>ARCANUM_NO_COLOR</c>,
    /// and a prompt that shows a default value (the <c>daemon install</c> service account) draws it
    /// in green. The prompt console therefore decides colour itself, on stderr, with the provider
    /// detector of a GitHub Actions job live: <c>GITHUB_ACTIONS=true</c> must not bring escapes back.
    /// </summary>
    [Theory]
    [InlineData("ARCANUM_NO_COLOR")]
    [InlineData("NO_COLOR")]
    public void A_no_colour_variable_keeps_the_prompt_console_free_of_ansi_inside_GitHub_Actions(string variable)
    {
        using EnvironmentScope environment = new(
            gitHubActions: "true",
            noColor: variable == "NO_COLOR" ? "1" : null,
            arcanumNoColor: variable == "ARCANUM_NO_COLOR" ? "1" : null);

        string written = WritePromptDefault(new CliInvocationOptions(Json: false, Plain: false, Yes: false), out AnsiConsoleSettings settings);

        Assert.Equal(AnsiSupport.No, settings.Ansi);

        Assert.Equal(ColorSystemSupport.NoColors, settings.ColorSystem);

        Assert.Equal(InteractionSupport.Yes, settings.Interactive);

        Assert.Contains(@"(HOST\me)", written, StringComparison.Ordinal);

        Assert.DoesNotContain('\u001b', written);
    }

    /// <summary>
    /// The control. Without it the no-colour cases above could pass because the GitHub Actions
    /// detector never ran, which would leave them proving nothing.
    /// </summary>
    [Fact]
    public void Control_a_prompt_console_with_no_opt_out_is_forced_to_ansi_by_the_GitHub_Actions_detector()
    {
        using EnvironmentScope environment = new(gitHubActions: "true", noColor: null, arcanumNoColor: null);

        string written = WritePromptDefault(new CliInvocationOptions(Json: false, Plain: false, Yes: false), out AnsiConsoleSettings settings);

        Assert.Equal(AnsiSupport.Detect, settings.Ansi);

        Assert.Contains('\u001b', written);
    }

    /// <summary>
    /// Writes a default value the way <c>daemon install</c>'s account prompt does, through a console
    /// built from <see cref="SensitiveValueInput.CreatePromptSettings"/> over a swapped
    /// <see cref="Console.Error"/>, and returns what reached standard error.
    /// </summary>
    private static string WritePromptDefault(CliInvocationOptions options, out AnsiConsoleSettings settings)
    {
        TextWriter originalError = Console.Error;

        StringWriter stderr = new();

        Console.SetError(stderr);

        try
        {
            settings = SensitiveValueInput.CreatePromptSettings(options);

            CliConsoleFactory.Create(settings).Write(new Markup(@"[green bold](HOST\me)[/]"));
        }
        finally
        {
            Console.SetError(originalError);
        }

        return stderr.ToString();
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _originalGitHubActions = System.Environment.GetEnvironmentVariable("GITHUB_ACTIONS");

        private readonly string? _originalNoColor = System.Environment.GetEnvironmentVariable("NO_COLOR");

        private readonly string? _originalArcanumNoColor =
            System.Environment.GetEnvironmentVariable("ARCANUM_NO_COLOR");

        internal EnvironmentScope(string? gitHubActions, string? noColor, string? arcanumNoColor)
        {
            System.Environment.SetEnvironmentVariable("GITHUB_ACTIONS", gitHubActions);

            System.Environment.SetEnvironmentVariable("NO_COLOR", noColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", arcanumNoColor);
        }

        public void Dispose()
        {
            System.Environment.SetEnvironmentVariable("GITHUB_ACTIONS", _originalGitHubActions);

            System.Environment.SetEnvironmentVariable("NO_COLOR", _originalNoColor);

            System.Environment.SetEnvironmentVariable("ARCANUM_NO_COLOR", _originalArcanumNoColor);
        }
    }

    /// <summary>
    /// R-335: the hidden prompt's question is a diagnostic, so it goes to stderr like
    /// <c>BackupPassphraseReader</c>'s. Written to stdout it lands in whatever the operator redirected
    /// stdout into, and under <c>--json</c> it would be part of the document.
    /// </summary>
    [Fact]
    public void CreatePromptSettings_writes_to_stderr()
    {
        TextWriter originalOut = Console.Out;

        TextWriter originalError = Console.Error;

        StringWriter stdout = new();

        StringWriter stderr = new();

        Console.SetOut(stdout);

        Console.SetError(stderr);

        try
        {
            AnsiConsoleSettings settings = SensitiveValueInput.CreatePromptSettings(
                new CliInvocationOptions(Json: false, Plain: true, Yes: false));

            CliConsoleFactory.Create(settings).WriteLine("Master API key:");

            Assert.Contains("Master API key:", stderr.ToString(), StringComparison.Ordinal);

            Assert.Empty(stdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);

            Console.SetError(originalError);
        }
    }

    /// <summary>
    /// Credential normalisation is a decision, not an accident of refactoring: the baseline trimmed only the redirected route, so the same key typed at the hidden prompt and piped in produced different stored bytes. Both routes converge here. Pinned because it decides the bytes stored as a credential, and because the neighbouring <c>config set</c> route deliberately keeps the opposite answer.
    /// </summary>
    [Fact]
    public async Task A_credential_typed_at_the_hidden_prompt_is_normalised_like_a_piped_one()
    {
        FakeSensitiveValueConsole prompted = new(isInputRedirected: false, line: null)
        {
            PromptResult = "  sk-typed-secret\t",
        };

        SensitiveValueRead promptRead = SensitiveValueInput.NormalizeCredential(
            await SensitiveValueInput.ReadAsync(
                prompted,
                new CliInvocationOptions(Json: false, Plain: false, Yes: false),
                "Master API key:",
                CancellationToken.None));

        FakeSensitiveValueConsole piped = new(
            isInputRedirected: true,
            line: "  sk-typed-secret\t");

        SensitiveValueRead pipedRead = SensitiveValueInput.NormalizeCredential(
            await SensitiveValueInput.ReadAsync(
                piped,
                new CliInvocationOptions(Json: false, Plain: false, Yes: false),
                "Master API key:",
                CancellationToken.None));

        Assert.Equal("sk-typed-secret", promptRead.Value);

        Assert.Equal(promptRead.Value, pipedRead.Value);
    }

    /// <summary>
    /// "No input route exists" is a configuration error, not an empty credential. Normalisation must not launder it into an available-but-blank read, which the caller would report as an empty key instead of naming the missing route.
    /// </summary>
    [Fact]
    public void Normalisation_leaves_an_unavailable_read_unavailable()
    {
        SensitiveValueRead normalized =
            SensitiveValueInput.NormalizeCredential(SensitiveValueRead.Unavailable);

        Assert.False(normalized.IsAvailable);

        Assert.Null(normalized.Value);
    }

    private sealed class FakeSensitiveValueConsole(bool isInputRedirected, string? line)
        : ISensitiveValueConsole
    {
        public bool PromptWasShown { get; private set; }

        public string PromptResult { get; init; } = string.Empty;

        public bool IsInputRedirected => isInputRedirected;

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(line);

        public string PromptHidden(string prompt, CliInvocationOptions options)
        {
            PromptWasShown = true;

            return PromptResult;
        }

        public string PromptVisible(string prompt, string defaultValue, CliInvocationOptions options) =>
            defaultValue;
    }
}
