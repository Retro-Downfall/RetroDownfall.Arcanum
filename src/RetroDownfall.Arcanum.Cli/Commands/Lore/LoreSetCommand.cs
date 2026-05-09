using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreSetCommand(ArcanumApiClient apiClient) : AsyncCommand
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
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Error.Message)}[/]");

            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Successfully scribed lore for '{Markup.Escape(Key)}'.[/]");

        return 0;
    }
}
