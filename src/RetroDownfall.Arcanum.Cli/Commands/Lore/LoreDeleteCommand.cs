using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreDeleteCommand(ArcanumApiClient apiClient) : AsyncCommand
{
    [CommandArgument(0, "<KEY>")]
    public required string Key { get; init; }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Result<bool> result = await apiClient.DeleteLoreAsync(Key, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Error.Message)}[/]");

            return 1;
        }

        if (result.Value)
        {
            AnsiConsole.MarkupLine($"[grey]Deleted lore for '{Markup.Escape(Key)}'.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No lore entry found for '{Markup.Escape(Key)}'; nothing was deleted.[/]");
        }

        return 0;
    }
}
