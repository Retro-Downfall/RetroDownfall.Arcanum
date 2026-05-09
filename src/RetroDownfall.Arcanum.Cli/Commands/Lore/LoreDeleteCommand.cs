using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{
    [CommandArgument(0, "<KEY>")]
    public required string Key { get; init; }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Result<bool> result = await apiClient.DeleteLoreAsync(Key, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(result.Error.Message)));

            return 1;
        }

        if (result.Value)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape($"Deleted lore for '{Key}'.")));
        }
        else
        {
            AnsiConsole.MarkupLine(
                themePalette.HighlightMarkup(
                    Markup.Escape($"No lore entry found for '{Key}'; nothing was deleted.")));
        }

        return 0;
    }
}
