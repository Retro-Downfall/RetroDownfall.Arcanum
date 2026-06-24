using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreSetCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand<LoreSetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<LoreDto> result =
            await apiClient.UpsertLoreAsync(settings.Key, settings.Value, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"Successfully scribed lore for '{settings.Key}'.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<KEY>")]
        public required string Key { get; init; }

        [CommandArgument(1, "<VALUE>")]
        public required string Value { get; init; }

    }

}
