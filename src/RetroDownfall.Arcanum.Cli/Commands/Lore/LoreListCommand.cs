using System.Globalization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

public sealed class LoreListCommand(ArcanumApiClient apiClient) : AsyncCommand
{
    private const int SnippetMaxLength = 50;

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Result<List<LoreDto>> result = await apiClient.ListLoreAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(result.Error.Message)}[/]");

            return 1;
        }

        Table table = new();

        table.AddColumn("Key");
        table.AddColumn("Updated (UTC)");
        table.AddColumn("Value Snippet");

        foreach (LoreDto row in result.Value)
        {
            string snippet = row.Value.Length <= SnippetMaxLength
                ? row.Value
                : string.Concat(row.Value.AsSpan(0, SnippetMaxLength), "...");

            table.AddRow(
                Markup.Escape(row.Key),
                Markup.Escape(row.UpdatedAtUtc.ToString("u", CultureInfo.InvariantCulture)),
                Markup.Escape(snippet));
        }

        AnsiConsole.Write(table);

        return 0;
    }
}
