using System.Globalization;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

/// <summary>
/// RAG Phase 2 — <c>arcanum session divine &lt;QUERY&gt;</c>: semantic search over Grimoire entries
/// (POST /api/sessions/divine).
/// </summary>
public sealed class SessionDivinationCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SessionDivinationCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Query))
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<QUERY> is required.")));

            return 1;

        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.Campaign))
        {

            if (!CliArgReader.TryParseGuid(settings.Campaign, out Guid parsedCampaignId))
            {

                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaign must be a valid GUID.")));

                return 1;

            }

            campaignId = parsedCampaignId;

        }

        SemanticSearchRequest request = new(settings.Query.Trim(), campaignId, settings.Status, settings.Limit);

        Result<SemanticSearchResult> result = await apiClient
            .DivineSessionsAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;

        }

        SemanticSessionSearchResult[] hits = result.Value.Results;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Session ID")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Title")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Role")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Similarity")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Created")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Content Preview")));

        foreach (SemanticSessionSearchResult hit in hits)
        {

            string shortId = hit.SessionId.ToString("D")[..8];

            string similarityPercent = (hit.Similarity * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(shortId))),
                new Markup(themePalette.TextMarkup(Markup.Escape(string.IsNullOrWhiteSpace(hit.SessionTitle) ? "(untitled)" : hit.SessionTitle))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(hit.EntryRole))),
                new Markup(themePalette.HighlightMarkup(Markup.Escape(similarityPercent))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(hit.EntryCreatedAt.ToString("u", CultureInfo.InvariantCulture)))),
                new Markup(themePalette.TextMarkup(Markup.Escape(hit.EntryContentPreview))));

        }

        AnsiConsole.Write(table);

        if (hits.Length == 0)
        {

            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No sessions matched.")));

        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<QUERY>")]
        public required string Query { get; init; }

        [CommandOption("--limit <LIMIT>")]
        public int? Limit { get; init; }

        [CommandOption("--campaign <CAMPAIGN>")]
        public string? Campaign { get; init; }

        [CommandOption("--status <STATUS>")]
        public string? Status { get; init; }

    }

}
