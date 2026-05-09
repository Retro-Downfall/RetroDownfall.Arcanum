using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette) : AsyncCommand
{
    [CommandArgument(0, "<KEY>")]
    public required string Key { get; init; }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Result<LoreDto> result = await apiClient.GetLoreAsync(Key, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(result.Error.Message)));

            return 1;
        }

        Panel panel = new(new Markup(Markup.Escape(result.Value.Value)))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Lore: {Key}"))),
        };

        AnsiConsole.Write(panel);

        return 0;
    }
}
