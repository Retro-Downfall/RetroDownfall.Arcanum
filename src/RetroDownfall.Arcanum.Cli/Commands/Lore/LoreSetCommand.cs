using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreSetCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{
    [CommandArgument(0, "<KEY>")]
    public required string Key { get; init; }

    [CommandArgument(1, "<VALUE>")]
    public required string Value { get; init; }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Result<LoreDto> result =
            await apiClient.UpsertLoreAsync(Key, Value, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(result.Error.Message)));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"Successfully scribed lore for '{Key}'.")));

        return 0;
    }
}
