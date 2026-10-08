using RetroDownfall.Arcanum.Cli.Commands;

using RetroDownfall.Arcanum.Cli.Commands.Daemon;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Hosting;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The service account and its password never travel on the command line: they come from the terminal prompt (the
/// password hidden) or from redirected stdin, and a headless invocation with neither has no route and refuses.
/// </summary>
public sealed class DaemonServiceAccountPromptTests
{
    private static readonly CliInvocationOptions Interactive = new(Json: false, Plain: false, Yes: false);

    [Fact]
    public async Task Redirected_stdin_supplies_the_account_on_the_first_line_and_the_password_on_the_second()
    {
        ScriptedConsole console = new(isInputRedirected: true, lines: [@"  HOST\me  ", " pass word "]);

        Result<DaemonServiceCredential> result = await CreatePrompt(console, Interactive)
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(@"HOST\me", result.Value.AccountName);

        // An account name is trimmed; a password is not, because whitespace can be part of one.
        Assert.Equal(" pass word ", result.Value.Password);

        Assert.Equal(0, console.PromptsShown);
    }

    [Fact]
    public async Task A_terminal_asks_for_the_account_in_the_clear_and_the_password_hidden()
    {
        ScriptedConsole console = new(isInputRedirected: false, lines: [])
        {
            VisibleAnswer = @"HOST\me",

            HiddenAnswer = "typed secret",
        };

        Result<DaemonServiceCredential> result = await CreatePrompt(console, Interactive)
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);

        Assert.Equal(@"HOST\me", result.Value.AccountName);

        Assert.Equal("typed secret", result.Value.Password);

        Assert.Equal(1, console.VisiblePrompts);

        Assert.Equal(1, console.HiddenPrompts);

        Assert.Contains(@"\", console.VisibleDefault, StringComparison.Ordinal);

        Assert.DoesNotContain("typed secret", console.LastHiddenPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_headless_invocation_with_no_redirected_stdin_refuses_instead_of_prompting(bool print, bool json)
    {
        ScriptedConsole console = new(isInputRedirected: false, lines: []);

        Result<DaemonServiceCredential> result = await CreatePrompt(
                console,
                new CliInvocationOptions(Json: json, Plain: false, Yes: false, Print: print))
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(DaemonServiceAccountPrompt.UnavailableCode, result.Error.Code);

        Assert.Contains("Task Scheduler", result.Error.Message, StringComparison.Ordinal);

        Assert.Equal(0, console.PromptsShown);
    }

    [Fact]
    public async Task Redirected_stdin_is_read_even_when_a_headless_marker_is_present()
    {
        ScriptedConsole console = new(isInputRedirected: true, lines: [@"HOST\me", "pw"]);

        Result<DaemonServiceCredential> result = await CreatePrompt(
                console,
                new CliInvocationOptions(Json: true, Plain: false, Yes: false, Print: true))
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_account_is_refused_before_the_password_is_read(string? account)
    {
        ScriptedConsole console = new(isInputRedirected: true, lines: [account, "pw"]);

        Result<DaemonServiceCredential> result = await CreatePrompt(console, Interactive)
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(DaemonServiceAccountPrompt.InvalidCode, result.Error.Code);

        Assert.Equal(1, console.LinesRead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_empty_password_is_refused(string? password)
    {
        ScriptedConsole console = new(isInputRedirected: true, lines: [@"HOST\me", password]);

        Result<DaemonServiceCredential> result = await CreatePrompt(console, Interactive)
            .ReadAsync(CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(DaemonServiceAccountPrompt.InvalidCode, result.Error.Code);
    }

    [Fact]
    public void A_credential_never_prints_its_password()
    {
        DaemonServiceCredential credential = new(@"HOST\me", "do-not-print");

        Assert.DoesNotContain("do-not-print", credential.ToString(), StringComparison.Ordinal);

        Assert.DoesNotContain("do-not-print", new DaemonInstallRequest(credential).ToString(), StringComparison.Ordinal);

        Assert.Contains(@"HOST\me", credential.ToString(), StringComparison.Ordinal);
    }

    private static DaemonServiceAccountPrompt CreatePrompt(ScriptedConsole console, CliInvocationOptions options) =>
        new(new FixedInvocationContext(options), console);

    private sealed class FixedInvocationContext(CliInvocationOptions options) : ICliInvocationContext
    {
        public CliInvocationOptions Options { get; } = options;
    }

    private sealed class ScriptedConsole(bool isInputRedirected, string?[] lines) : ISensitiveValueConsole
    {
        private int _next;

        public string VisibleAnswer { get; init; } = string.Empty;

        public string HiddenAnswer { get; init; } = string.Empty;

        public int LinesRead => _next;

        public int VisiblePrompts { get; private set; }

        public int HiddenPrompts { get; private set; }

        public int PromptsShown => VisiblePrompts + HiddenPrompts;

        public string VisibleDefault { get; private set; } = string.Empty;

        public string LastHiddenPrompt { get; private set; } = string.Empty;

        public bool IsInputRedirected => isInputRedirected;

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            string? line = _next < lines.Length ? lines[_next] : null;

            _next++;

            return Task.FromResult(line);
        }

        public string PromptHidden(string prompt, CliInvocationOptions options)
        {
            HiddenPrompts++;

            LastHiddenPrompt = prompt;

            return HiddenAnswer;
        }

        public string PromptVisible(string prompt, string defaultValue, CliInvocationOptions options)
        {
            VisiblePrompts++;

            VisibleDefault = defaultValue;

            return VisibleAnswer;
        }
    }
}
