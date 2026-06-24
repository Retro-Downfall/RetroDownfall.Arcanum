using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand<LoreGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<LoreDto> result = await apiClient.GetLoreAsync(settings.Key, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        Panel panel = new(new Markup(Markup.Escape(result.Value.Value)))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Lore: {settings.Key}"))),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<KEY>")]
        public required string Key { get; init; }

    }

}
