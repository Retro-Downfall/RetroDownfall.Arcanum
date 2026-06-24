using System.Diagnostics.CodeAnalysis;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Security;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands;

[ExcludeFromCodeCoverage] // Reason: thin ISecretStore read wrapper; filesystem-specific.
public sealed class KeyShowCommand(ISecretStore secretStore, IThemePalette themePalette) : AsyncCommand
{

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
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

        AnsiConsole.WriteLine(result.Value!);

        return 0;

    }

}
