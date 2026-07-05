using System.Diagnostics.CodeAnalysis;
using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Master API key utilities (local secret store only; no HTTP).
/// </summary>
[ExcludeFromCodeCoverage] // Reason: thin ISecretStore read wrapper; filesystem-specific.
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
                    Markup.Escape("No master API key found. Run 'arcanum serve' once to generate and store a key.")));

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
                Markup.Escape("(Key written to stderr so it is not captured by stdout piping.)")));

        return 0;

    }

}
