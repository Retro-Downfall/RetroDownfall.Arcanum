using RetroDownfall.Arcanum.Cli.Infrastructure;

using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Services.Setup;

public sealed record SetupChoice(string Id, string Label, string Description);

/// <summary>
/// Every question the wizard can ask. A <see langword="null"/> answer means the operator ended input
/// (Ctrl+D / EOF) or declined to choose; the wizard treats that exactly like Ctrl+C and leaves the
/// installation untouched.
/// </summary>
internal interface ISetupPrompt
{

    bool IsInteractive { get; }

    void Write(string line);

    Task<string?> AskAsync(
        string question,
        string? defaultValue,
        CancellationToken cancellationToken);

    Task<bool?> ConfirmAsync(
        string question,
        bool defaultValue,
        CancellationToken cancellationToken);

    Task<string?> SelectAsync(
        string question,
        IReadOnlyList<SetupChoice> choices,
        string? defaultId,
        CancellationToken cancellationToken);

    /// <summary>Reads a credential without echoing it. Never returns the value to any other sink.</summary>
    Task<string?> AskSecretAsync(string question, CancellationToken cancellationToken);

}

/// <summary>
/// Console prompt driver. Questions and progress go to stderr so a <c>--json</c> run still emits
/// exactly one document on stdout. When stdin is redirected the prompts degrade to plain line reads,
/// which keeps scripted runs and the masked interactive prompt on the same code path.
/// </summary>
internal sealed class ConsoleSetupPrompt(
    IConsoleDispatcher console,
    ICliInvocationContext invocationContext) : ISetupPrompt
{

    /// <summary>
    /// Rich prompts render through Spectre, which writes to stdout. Under <c>--json</c> that would
    /// interleave prompt text with the result document, so the plain stderr/stdin path is used
    /// instead — the run stays interactive, and stdout stays exactly one JSON document.
    /// </summary>
    public bool IsInteractive =>
        !Console.IsInputRedirected && !invocationContext.Options.Json;

    public void Write(string line) => console.WriteDiagnostic(line);

    public async Task<string?> AskAsync(
        string question,
        string? defaultValue,
        CancellationToken cancellationToken)
    {

        if (!IsInteractive)
        {

            console.WriteDiagnostic(
                defaultValue is null ? question : $"{question} [{defaultValue}]");

            string? line = await Console.In
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            return line is null
                ? null
                : string.IsNullOrWhiteSpace(line) ? defaultValue ?? string.Empty : line.Trim();

        }

        TextPrompt<string> prompt = new(question) { AllowEmpty = defaultValue is not null };

        if (defaultValue is not null)
        {

            _ = prompt.DefaultValue(defaultValue);

        }

        return AnsiConsole.Prompt(prompt);

    }

    public async Task<bool?> ConfirmAsync(
        string question,
        bool defaultValue,
        CancellationToken cancellationToken)
    {

        if (!IsInteractive)
        {

            console.WriteDiagnostic($"{question} [{(defaultValue ? "Y/n" : "y/N")}]");

            string? line = await Console.In
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {

                return null;

            }

            string trimmed = line.Trim();

            return trimmed.Length == 0
                ? defaultValue
                : trimmed.StartsWith('y') || trimmed.StartsWith('Y');

        }

        return AnsiConsole.Confirm(question, defaultValue);

    }

    public async Task<string?> SelectAsync(
        string question,
        IReadOnlyList<SetupChoice> choices,
        string? defaultId,
        CancellationToken cancellationToken)
    {

        if (choices.Count == 0)
        {

            return null;

        }

        if (!IsInteractive)
        {

            console.WriteDiagnostic(question);

            foreach (SetupChoice choice in choices)
            {

                console.WriteDiagnostic($"  {choice.Id} — {choice.Label}: {choice.Description}");

            }

            console.WriteDiagnostic($"Enter a choice [{defaultId ?? choices[0].Id}]");

            string? line = await Console.In
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            if (line is null)
            {

                return null;

            }

            string trimmed = line.Trim();

            return trimmed.Length == 0
                ? defaultId ?? choices[0].Id
                : choices.FirstOrDefault(choice =>
                        string.Equals(choice.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                    ?.Id
                    ?? trimmed;

        }

        SelectionPrompt<string> prompt = new()
        {

            Title = question,

        };

        _ = prompt.AddChoices(choices.Select(choice => $"{choice.Id} — {choice.Label}"));

        string selection = AnsiConsole.Prompt(prompt);

        return choices
            .FirstOrDefault(choice =>
                selection.StartsWith(choice.Id, StringComparison.Ordinal))
            ?.Id;

    }

    public async Task<string?> AskSecretAsync(
        string question,
        CancellationToken cancellationToken)
    {

        if (!IsInteractive)
        {

            console.WriteDiagnostic(question);

            string? line = await Console.In
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false);

            return line?.Trim();

        }

        return AnsiConsole.Prompt(new TextPrompt<string>(question).Secret().AllowEmpty());

    }

}
