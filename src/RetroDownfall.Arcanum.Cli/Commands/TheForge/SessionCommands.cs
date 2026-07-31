using System.Globalization;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

/// <summary>
/// Session semantic search (requires arcanum serve).
/// </summary>
public sealed class SessionCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    ICliResourceCatalog? resourceCatalog = null)
{
    public async Task<int> List(int? limit = null, CancellationToken cancellationToken = default)
    {
        Result<SessionQueryResult> result = await apiClient.QuerySessionsAsync(limit, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        Table table = new();
        table.AddColumn(themePalette.HeadingTableColumn("Title"));
        table.AddColumn(themePalette.HeadingTableColumn("Campaign"));
        table.AddColumn(themePalette.HeadingTableColumn("Status"));
        table.AddColumn(themePalette.HeadingTableColumn("Updated"));
        foreach (SessionSummaryDto session in result.Value.Summaries)
        {
            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(session.Title ?? "(untitled)"))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(session.CampaignId?.ToString("D") ?? "-"))),
                new Markup(themePalette.TextMarkup(Markup.Escape(session.Status))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(session.UpdatedAt.ToString("u", CultureInfo.InvariantCulture)))));
        }
        AnsiConsole.Write(table);
        return 0;
    }

    public async Task<int> Get(string? identifier, CancellationToken cancellationToken = default)
    {
        Guid id;
        if (!Guid.TryParse(identifier, out id))
        {
            if (resourceCatalog is null)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));
                return 1;
            }

            ResourceSelectionResult<SessionSummaryDto> selection = await resourceCatalog
                .SelectSessionAsync(identifier, cancellationToken)
                .ConfigureAwait(false);
            if (selection.Status == ResourceSelectionStatus.Cancelled)
            {
                return 0;
            }
            if (selection.Status == ResourceSelectionStatus.Error)
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(selection.Error!)));
                return 1;
            }
            id = selection.Value!.Id;
        }

        Result<SessionDetailDto> result = await apiClient.GetSessionAsync(id, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        SessionDetailDto session = result.Value;
        Table table = new Table().Border(TableBorder.None).HideHeaders();
        table.AddColumn(string.Empty);
        table.AddColumn(string.Empty);
        table.AddRow("Id:", Markup.Escape(session.Id.ToString("D")));
        table.AddRow("Title:", Markup.Escape(session.Title ?? "(untitled)"));
        table.AddRow("Campaign:", Markup.Escape(session.CampaignId?.ToString("D") ?? "-"));
        table.AddRow("Status:", Markup.Escape(session.Status));
        table.AddRow("Updated:", Markup.Escape(session.UpdatedAt.ToString("u", CultureInfo.InvariantCulture)));
        AnsiConsole.Write(table);
        return 0;
    }


    /// <summary>
    /// Semantic search over Grimoire entries (POST /api/sessions/divine; requires
    /// Arcanum:Features:SessionSearch plus configured Arcanum:Integrations:Embeddings:Provider and
    /// Arcanum:Integrations:Embeddings:Model facts).
    /// </summary>
    /// <param name="query">Search query text.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <param name="campaign">Filter by campaign GUID.</param>
    /// <param name="status">Filter by session status.</param>
    public async Task<int> Divine(
        string query,
        int? limit = null,
        string? campaign = null,
        string? status = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(query))
        {

            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<QUERY> is required.")));

            return 1;

        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(campaign))
        {

            if (!CliArgReader.TryParseGuid(campaign, out Guid parsedCampaignId))
            {

                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaign must be a valid GUID.")));

                return 1;

            }

            campaignId = parsedCampaignId;

        }

        SemanticSearchRequest request = new(query.Trim(), campaignId, status, limit);

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

}
