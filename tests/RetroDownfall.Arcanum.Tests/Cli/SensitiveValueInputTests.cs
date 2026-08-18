using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The hidden-credential input routes ('config set' on a sensitive key, 'key set',
/// 'key provider set') must pick between redirected stdin and the hidden prompt using the
/// invocation's headless markers, not <c>Console.IsInputRedirected</c> alone. A real terminal under
/// <c>--print</c>/<c>--output-format json</c> must behave like a redirected one — no prompt may
/// block — and <c>--plain</c> is a colour flag that must keep the documented hidden prompt alive.
/// </summary>
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

        AnsiConsoleSettings styled = SensitiveValueInput.CreatePromptSettings(
            new CliInvocationOptions(Json: false, Plain: false, Yes: false));

        Assert.Equal(InteractionSupport.Yes, styled.Interactive);

        Assert.Equal(AnsiSupport.Detect, styled.Ansi);

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

    }

}
