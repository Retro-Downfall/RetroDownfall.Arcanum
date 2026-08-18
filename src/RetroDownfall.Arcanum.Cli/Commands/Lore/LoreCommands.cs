using System.Globalization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Lore;

/// <summary>
/// Manage Grimoire explicit memory (lore) directly.
/// </summary>
public sealed class LoreCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConsoleDispatcher console)
{

    private const int SnippetMaxLength = 50;

    /// <summary>
    /// List all scribed lore keys.
    /// </summary>
    public async Task<int> List(CancellationToken cancellationToken)
    {
        Result<List<LoreDto>> result = await apiClient.ListLoreAsync(cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        Table table = new();

        table.AddColumn("Key");
        table.AddColumn("Updated (UTC)");
        table.AddColumn("Value Snippet");

        foreach (LoreDto row in result.Value)
        {
            string? value = row.Value;

            string snippet = string.IsNullOrEmpty(value) || value.Length <= SnippetMaxLength
                ? value ?? string.Empty
                : string.Concat(value.AsSpan(0, SnippetMaxLength), "...");

            table.AddRow(
                Markup.Escape(row.Key),
                Markup.Escape(row.UpdatedAtUtc.ToString("u", CultureInfo.InvariantCulture)),
                Markup.Escape(snippet));
        }

        AnsiConsole.Write(table);

        return 0;
    }

    /// <summary>
    /// Read a specific lore entry by key.
    /// </summary>
    /// <param name="key">The lore key.</param>
    public async Task<int> Get(string key, CancellationToken cancellationToken)
    {

        Result<LoreDto> result = await apiClient.GetLoreAsync(key, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        // The key on the diagnostic stream, the value verbatim on the payload stream. A Spectre
        // panel would draw a border around the value and re-flow it at the profile width (80 when
        // stdout is redirected), so `VALUE=$(arcanum lore get k)` captured box art and a multi-line
        // value could not survive a set/get round-trip.
        console.WriteDiagnostic($"Lore: {key}");

        await Console.Out.WriteLineAsync(result.Value.Value).ConfigureAwait(false);

        return 0;

    }

    /// <summary>
    /// Create or update a lore entry.
    /// </summary>
    /// <param name="key">The lore key.</param>
    /// <param name="value">The lore value.</param>
    public async Task<int> Set(string key, string value, CancellationToken cancellationToken)
    {

        Result<LoreDto> result =
            await apiClient.UpsertLoreAsync(key, value, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"Successfully scribed lore for '{key}'.")));

        return 0;

    }

    /// <summary>
    /// Delete a lore entry.
    /// </summary>
    /// <param name="key">The lore key.</param>
    public async Task<int> Delete(string key, CancellationToken cancellationToken)
    {

        Result<bool> result = await apiClient.DeleteLoreAsync(key, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {

            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape($"Deleted lore for '{key}'.")));

        return 0;

    }

}
