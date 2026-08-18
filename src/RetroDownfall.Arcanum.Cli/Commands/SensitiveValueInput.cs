using RetroDownfall.Arcanum.Cli.Infrastructure;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Console seam for the hidden-credential input routes. Kept behind an interface so the three-way
/// choice between redirected stdin, the hidden prompt, and "cannot be obtained" is testable without
/// a real terminal.
/// </summary>
internal interface ISensitiveValueConsole
{

    bool IsInputRedirected { get; }

    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    string PromptHidden(string prompt, CliInvocationOptions options);

}

/// <summary>
/// Outcome of asking for a sensitive value. <see cref="IsAvailable"/> is <c>false</c> only when no
/// input route exists for this invocation, which is a configuration error rather than an empty
/// value.
/// </summary>
internal readonly record struct SensitiveValueRead(bool IsAvailable, string? Value)
{

    internal static SensitiveValueRead Unavailable { get; } = new(false, null);

    internal static SensitiveValueRead From(string? value) => new(true, value);

}

internal static class SensitiveValueInput
{

    internal const string UnavailableDiagnostic =
        "A sensitive value must be supplied on redirected stdin when --print or "
        + "--output-format json is in effect; the hidden prompt cannot run headless.";

    /// <summary>
    /// Three-way choice. Redirected stdin always wins, so redirecting a value in keeps working even
    /// when stdout is a file. Otherwise stdin is a terminal, and the hidden prompt runs unless the
    /// invocation carries an explicit headless marker — <c>--print</c> or <c>--output-format
    /// json</c>, both of which promise that no prompt blocks. <c>--plain</c> is deliberately absent:
    /// it only strips colour, and the hidden prompt is a documented input route under it.
    /// </summary>
    internal static async Task<SensitiveValueRead> ReadAsync(
        ISensitiveValueConsole console,
        CliInvocationOptions options,
        string prompt,
        CancellationToken cancellationToken)
    {

        if (console.IsInputRedirected)
        {

            return SensitiveValueRead.From(
                await console.ReadLineAsync(cancellationToken).ConfigureAwait(false));

        }

        if (options.Print || options.Json)
        {

            return SensitiveValueRead.Unavailable;

        }

        return SensitiveValueRead.From(console.PromptHidden(prompt, options));

    }

    /// <summary>
    /// Prompt settings for a stdin that is known to be a terminal. Interaction is asserted rather
    /// than detected because <c>ConfigureAnsiConsoleForInvocation</c> disables it for the whole
    /// process under <c>--plain</c>, which would otherwise make Spectre throw
    /// <see cref="InvalidOperationException"/> the moment the hidden prompt reads a key.
    /// </summary>
    internal static AnsiConsoleSettings CreatePromptSettings(CliInvocationOptions options) =>
        new()
        {

            Ansi = options.Plain ? AnsiSupport.No : AnsiSupport.Detect,

            ColorSystem = options.Plain ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,

            Interactive = InteractionSupport.Yes,

            Out = new AnsiConsoleOutput(Console.Out),

        };

}

internal sealed class SystemSensitiveValueConsole : ISensitiveValueConsole
{

    internal static SystemSensitiveValueConsole Instance { get; } = new();

    public bool IsInputRedirected => Console.IsInputRedirected;

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        Console.In.ReadLineAsync(cancellationToken).AsTask();

    public string PromptHidden(string prompt, CliInvocationOptions options) =>
        AnsiConsole
            .Create(SensitiveValueInput.CreatePromptSettings(options))
            .Prompt(new TextPrompt<string>(prompt).Secret());

}
