using System.Diagnostics.CodeAnalysis;
using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Master API key utilities (local OS credential store / security.dat fallback; no HTTP).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin ISecretStore read/write wrapper; filesystem/OS-specific.
public sealed class KeyCommands(ISecretStore secretStore, IThemePalette themePalette)
{

    /// <summary>
    /// Print the stored master API key to stderr (so stdout piping does not capture the secret).
    /// </summary>
    [Command("show")]
    public async Task<int> Show(CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        SecretStoreReadResult result = await secretStore.GetApiKeyReadResultAsync().ConfigureAwait(false);

        if (result.Status == SecretStoreReadStatus.Missing)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(
                        "No master API key found. Run 'arcanum serve' once to generate and store a key, "
                        + "or 'arcanum key set' to paste one into the OS credential store.")));

            return 1;

        }

        if (result.Status == SecretStoreReadStatus.Corrupted)
        {

            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(result.Message ?? "security.dat is present but could not be decrypted.")));

            return 1;

        }

        Console.Error.WriteLine(result.Value!);

        AnsiConsole.MarkupLine(
            themePalette.MutedMarkup(
                Markup.Escape(
                    $"(Key written to stderr. Shared OS identity: {ArcanumCredentialIdentity.Service}/"
                    + $"{ArcanumCredentialIdentity.MasterApiKeyAccount}.)")));

        return 0;

    }

    /// <summary>
    /// Store a master API key in the OS credential store (and mirror to security.dat when possible).
    /// Pass the key as an argument, or omit it to read a single line from stdin.
    /// </summary>
    [Command("set")]
    public async Task<int> Set(CancellationToken cancellationToken, [Argument] string? apiKey = null)
    {

        cancellationToken.ThrowIfCancellationRequested();

        string? key = apiKey;

        if (string.IsNullOrWhiteSpace(key))
        {

            if (Console.IsInputRedirected)
            {

                key = (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();

            }
            else
            {

                key = AnsiConsole.Prompt(
                    new TextPrompt<string>("Master API key:")
                        .Secret());

            }

        }

        if (string.IsNullOrWhiteSpace(key))
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup("API key must not be empty."));

            return 1;

        }

        await secretStore.SaveApiKeyAsync(key.Trim()).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(
                Markup.Escape(
                    $"Master API key stored ({ArcanumCredentialIdentity.Service}/"
                    + $"{ArcanumCredentialIdentity.MasterApiKeyAccount}).")));

        return 0;

    }

}
