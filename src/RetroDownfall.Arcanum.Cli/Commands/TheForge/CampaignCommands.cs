using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

internal static class CampaignCommandSupport
{

    public static bool TryParseWorkspaceType(string? value, out WorkspaceType type)
    {

        switch (value?.Trim().ToLowerInvariant())
        {

            case "spell":
                type = WorkspaceType.Spell;
                return true;

            case "campaign":
                type = WorkspaceType.Campaign;
                return true;

            case "data":
                type = WorkspaceType.Data;
                return true;

            case "custom":
                type = WorkspaceType.Custom;
                return true;

            default:
                type = default;
                return false;

        }

    }

    public static void WriteCampaignDetailPanel(CampaignDto campaign, IThemePalette themePalette)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Id:")), themePalette.TextMarkup(Markup.Escape(campaign.Id.ToString("D"))));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Name:")), themePalette.HighlightMarkup(Markup.Escape(campaign.Name)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Path:")), themePalette.TextMarkup(Markup.Escape(campaign.Path)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Type:")), themePalette.TextMarkup(Markup.Escape(campaign.Type.ToString())));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Description:")),
            themePalette.TextMarkup(Markup.Escape(campaign.Description ?? "(none)")));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Created:")),
            themePalette.TextMarkup(Markup.Escape(campaign.CreatedAt.ToString("u"))));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Updated:")),
            themePalette.TextMarkup(Markup.Escape(campaign.UpdatedAt.ToString("u"))));

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Campaign: {campaign.Name}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(panel);

    }

}

/// <summary>
/// The Forge campaign registry (requires arcanum serve).
/// </summary>
public sealed class CampaignCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// List registered campaigns (GET /api/campaigns).
    /// </summary>
    /// <param name="type">Filter by workspace type: spell, campaign, data, custom.</param>
    [Command("list")]
    public async Task<int> List(string? type = null, CancellationToken cancellationToken = default)
    {

        WorkspaceType? workspaceType = null;

        if (!string.IsNullOrWhiteSpace(type))
        {

            if (!CampaignCommandSupport.TryParseWorkspaceType(type, out WorkspaceType parsed))
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape("--type must be one of: spell, campaign, data, custom.")));

                return 1;
            }

            workspaceType = parsed;

        }

        Result<ListPageResult<CampaignDto>> result = await apiClient.GetCampaignsAsync(workspaceType, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        CampaignDto[] campaigns = result.Value.Items;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Name")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Path")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Type")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Created")));

        foreach (CampaignDto campaign in campaigns)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(campaign.Name))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(campaign.Path))),
                new Markup(themePalette.TextMarkup(Markup.Escape(campaign.Type.ToString()))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(campaign.CreatedAt.ToString("u")))));

        }

        AnsiConsole.Write(table);

        if (campaigns.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No campaigns are registered.")));
        }

        return 0;

    }

    /// <summary>
    /// Show campaign detail (GET /api/campaigns/{id}).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    [Command("get")]
    public async Task<int> Get([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<CampaignDto> result = await apiClient.GetCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        CampaignCommandSupport.WriteCampaignDetailPanel(result.Value, themePalette);

        return 0;

    }

    /// <summary>
    /// Register a new campaign (POST /api/campaigns).
    /// </summary>
    /// <param name="name">Campaign display name.</param>
    /// <param name="path">Absolute filesystem path to register as the campaign root.</param>
    /// <param name="type">Workspace type: spell, campaign, data, custom. Defaults to campaign.</param>
    /// <param name="description">Optional campaign description.</param>
    [Command("create")]
    public async Task<int> Create(
        string? name = null,
        string? path = null,
        string? type = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--path is required.")));

            return 1;
        }

        string typeText = string.IsNullOrWhiteSpace(type) ? "campaign" : type;

        if (!CampaignCommandSupport.TryParseWorkspaceType(typeText, out WorkspaceType workspaceType))
        {
            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("--type must be one of: spell, campaign, data, custom.")));

            return 1;
        }

        RegisterCampaignRequest request = new(name.Trim(), path.Trim(), workspaceType, description);

        Result<CampaignDto> result = await apiClient.CreateCampaignAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Campaign created:"),
                Markup.Escape($"{result.Value.Name} ({result.Value.Id:D})")));

        return 0;

    }

    /// <summary>
    /// Update a campaign (PUT /api/campaigns/{id}).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="name">New campaign display name.</param>
    [Command("update")]
    public async Task<int> Update([Argument] string id, string? name = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        UpdateCampaignRequest request = new(name, null, null, null);

        Result<CampaignDto> result = await apiClient.UpdateCampaignAsync(campaignId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Campaign updated:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    /// <summary>
    /// Remove a campaign (DELETE /api/campaigns/{id}).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    [Command("delete")]
    public async Task<int> Delete([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeleteCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Campaign removed.")));

        return 0;

    }

    /// <summary>
    /// Export a campaign's spells and prompts as JSON (POST /api/campaigns/{id}/export).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="output">Write exported JSON to this file instead of stdout.</param>
    [Command("export")]
    public async Task<int> Export([Argument] string id, string? output = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<CampaignExportDto> result = await apiClient.ExportCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        string json = System.Text.Json.JsonSerializer.Serialize(
            result.Value,
            RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.CampaignExportDto);

        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(output, json, cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                themePalette.HighlightLabelMarkup(Markup.Escape("Campaign exported to:"), Markup.Escape(output)));
        }

        return 0;

    }

    /// <summary>
    /// Import spells and prompts into a campaign (POST /api/campaigns/{id}/import).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="file">Path to a campaign export JSON file (as produced by 'campaign export').</param>
    [Command("import")]
    public async Task<int> Import([Argument] string id, string? file = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--file is required.")));

            return 1;
        }

        string json;

        try
        {
            json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Could not read file '{file}': {ex.Message}")));

            return 1;
        }

        CampaignExportDto? payload;

        try
        {
            payload = System.Text.Json.JsonSerializer.Deserialize(
                json,
                RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.CampaignExportDto);
        }
        catch (System.Text.Json.JsonException ex)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid campaign export JSON: {ex.Message}")));

            return 1;
        }

        if (payload is null)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Campaign export JSON parsed to an empty payload.")));

            return 1;
        }

        CampaignImportRequest request = new("merge", payload);

        Result<CampaignImportResultDto> result = await apiClient.ImportCampaignAsync(campaignId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        CampaignImportResultDto imported = result.Value;

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Campaign imported:"),
                Markup.Escape($"{imported.SpellsImported} spell(s), {imported.PromptsImported} prompt(s).")));

        foreach (string warning in imported.Warnings)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape($"Warning: {warning}")));
        }

        return 0;

    }

    /// <summary>
    /// List spells scoped to a campaign, shadowing built-ins (GET /api/campaigns/{id}/spells).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="query">-q, Free-text query.</param>
    /// <param name="tag">Filter by tag.</param>
    /// <param name="tool">Filter by declared tool.</param>
    [Command("spells")]
    public async Task<int> Spells(
        [Argument] string id,
        string? query = null,
        string? tag = null,
        string? tool = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<SpellSummary[]> result = await apiClient
            .GetCampaignSpellsAsync(campaignId, query, tag, tool, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        SpellCommandSupport.WriteSpellSummaryTable(result.Value, themePalette);

        return 0;

    }

    /// <summary>
    /// List prompts scoped to a campaign (GET /api/campaigns/{id}/prompts).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="query">-q, Free-text query.</param>
    /// <param name="tag">Filter by tag.</param>
    [Command("prompts")]
    public async Task<int> Prompts(
        [Argument] string id,
        string? query = null,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<ListPageResult<PromptSummaryDto>> result = await apiClient
            .GetCampaignPromptsAsync(campaignId, query, tag, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        PromptSummaryDto[] prompts = result.Value.Items;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Name")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Version")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Tags")));

        foreach (PromptSummaryDto prompt in prompts)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(prompt.Name))),
                new Markup(themePalette.TextMarkup(Markup.Escape(prompt.Version))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(prompt.Tags.Length == 0 ? "-" : string.Join(", ", prompt.Tags)))));

        }

        AnsiConsole.Write(table);

        if (prompts.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No prompts matched.")));
        }

        return 0;

    }

    /// <summary>
    /// List sessions scoped to a campaign (GET /api/campaigns/{id}/sessions).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="status">Filter by session status.</param>
    /// <param name="search">Free-text search.</param>
    /// <param name="limit">Maximum number of sessions to return.</param>
    /// <param name="beforeUpdatedAt">Pagination cursor: only sessions updated before this timestamp.</param>
    [Command("sessions")]
    public async Task<int> Sessions(
        [Argument] string id,
        string? status = null,
        string? search = null,
        int? limit = null,
        string? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        DateTimeOffset? beforeUpdatedAtParsed = null;

        if (!string.IsNullOrWhiteSpace(beforeUpdatedAt))
        {

            if (!DateTimeOffset.TryParse(
                    beforeUpdatedAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--before-updated-at must be a valid timestamp.")));

                return 1;
            }

            beforeUpdatedAtParsed = parsed;

        }

        Result<SessionQueryResult> result = await apiClient
            .GetCampaignSessionsAsync(campaignId, status, search, limit, beforeUpdatedAtParsed, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        SessionSummaryDto[] sessions = result.Value.Summaries;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("ID")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Title")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Status")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Entries")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Updated")));

        foreach (SessionSummaryDto session in sessions)
        {

            string shortId = session.Id.ToString("D")[..8];

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(shortId))),
                new Markup(themePalette.TextMarkup(Markup.Escape(string.IsNullOrWhiteSpace(session.Title) ? "(untitled)" : session.Title))),
                new Markup(themePalette.TextMarkup(Markup.Escape(session.Status))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(session.EntryCount.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(session.UpdatedAt.ToString("u")))));

        }

        AnsiConsole.Write(table);

        if (sessions.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No sessions matched.")));
        }

        if (result.Value.HasMore)
        {

            DateTimeOffset paginationCursor = result.Value.NextBeforeUpdatedAt ?? sessions[^1].UpdatedAt;

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape($"More results available \u2014 use --before-updated-at {paginationCursor:O} to page")));

        }

        return 0;

    }

}

/// <summary>
/// Manage the campaign's CODEX.md scratchpad.
/// </summary>
public sealed class CampaignCodexCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// Print CODEX.md (GET /api/campaigns/{id}/codex).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    [Command("get")]
    public async Task<int> Get([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<CodexContentDto> result = await apiClient.GetCampaignCodexAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        if (!result.Value.Exists)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No CODEX.md found for this campaign.")));

            return 0;
        }

        await Console.Out.WriteLineAsync(result.Value.Content).ConfigureAwait(false);

        return 0;

    }

    /// <summary>
    /// Write CODEX.md from a file (PUT /api/campaigns/{id}/codex).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="file">Path to a file whose contents become CODEX.md.</param>
    [Command("put")]
    public async Task<int> Put([Argument] string id, string? file = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--file is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile($"@{file}", out string content, out string? readError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(readError!)));

            return 1;
        }

        Result<CodexContentDto> result = await apiClient.PutCampaignCodexAsync(campaignId, content, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md updated.")));

        return 0;

    }

    /// <summary>
    /// Delete CODEX.md (DELETE /api/campaigns/{id}/codex).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    [Command("delete")]
    public async Task<int> Delete([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid campaignId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeleteCampaignCodexAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md removed.")));

        return 0;

    }

}
