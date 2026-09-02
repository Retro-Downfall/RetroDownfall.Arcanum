using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

internal static class CampaignCommandSupport
{
    public static async Task<(bool Resolved, bool Cancelled, Guid Id)> ResolveCampaignIdAsync(
        string? identifier,
        ICliResourceCatalog? resourceCatalog,
        IThemePalette themePalette,
        CancellationToken cancellationToken)
    {
        if (CliArgReader.TryParseGuid(identifier, out Guid id))
        {
            return (true, false, id);
        }

        if (resourceCatalog is null)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));
            return (false, false, default);
        }

        ResourceSelectionResult<CampaignDto> selection = await resourceCatalog
            .SelectCampaignAsync(identifier, cancellationToken)
            .ConfigureAwait(false);
        if (selection.Status == ResourceSelectionStatus.Cancelled)
        {
            return (false, true, default);
        }

        if (selection.Status == ResourceSelectionStatus.Error)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(selection.Error!)));
            return (false, false, default);
        }

        return (true, false, selection.Value!.Id);
    }

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
/// Campaign registry (requires arcanum serve).
/// </summary>
public sealed class CampaignCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConsoleDispatcher dispatcher,
    IConfirmationPrompt confirmationPrompt,
    IOptions<ArcanumSettings> settings,
    ICliResourceCatalog? resourceCatalog = null)
{

    private void WriteError(Error error) =>
        CliErrorOutput.WriteMarkupLine(
            themePalette.ErrorMarkup(CliFailureExit.Annotate(error, settings.Value.Host)));

    /// <summary>
    /// List registered campaigns (GET /api/campaigns).
    /// </summary>
    /// <param name="type">Filter by workspace type: spell, campaign, data, custom.</param>
    public async Task<int> List(string? type = null, CancellationToken cancellationToken = default)
    {

        WorkspaceType? workspaceType = null;

        if (!string.IsNullOrWhiteSpace(type))
        {

            if (!CampaignCommandSupport.TryParseWorkspaceType(type, out WorkspaceType parsed))
            {
                CliErrorOutput.WriteMarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape("--type must be one of: spell, campaign, data, custom.")));

                return (int)CliExitCode.ConfigurationError;
            }

            workspaceType = parsed;

        }

        Result<ListPageResult<CampaignDto>> result = await apiClient.GetCampaignsAsync(workspaceType, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
    public async Task<int> Get(string? id, CancellationToken cancellationToken)
    {
        Guid campaignId;
        if (!CliArgReader.TryParseGuid(id, out campaignId))
        {
            if (resourceCatalog is null)
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));
                return 1;
            }

            ResourceSelectionResult<CampaignDto> selection = await resourceCatalog
                .SelectCampaignAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (selection.Status == ResourceSelectionStatus.Cancelled)
            {
                return 0;
            }

            if (selection.Status == ResourceSelectionStatus.Error)
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(selection.Error!)));
                return 1;
            }

            campaignId = selection.Value!.Id;
        }

        Result<CampaignDto> result = await apiClient.GetCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
    public async Task<int> Create(
        string? name = null,
        string? path = null,
        string? type = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(name))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--path is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        string typeText = string.IsNullOrWhiteSpace(type) ? "campaign" : type;

        if (!CampaignCommandSupport.TryParseWorkspaceType(typeText, out WorkspaceType workspaceType))
        {
            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("--type must be one of: spell, campaign, data, custom.")));

            return (int)CliExitCode.ConfigurationError;
        }

        RegisterCampaignRequest request = new(name.Trim(), path.Trim(), workspaceType, description);

        Result<CampaignDto> result = await apiClient.CreateCampaignAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
    public async Task<int> Update(string? id, string? name = null, CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        UpdateCampaignRequest request = new(name, null, null, null);

        Result<CampaignDto> result = await apiClient.UpdateCampaignAsync(campaignId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Campaign updated:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    /// <summary>
    /// Remove a campaign (DELETE /api/campaigns/{id}).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    public async Task<int> Delete(string? id, CancellationToken cancellationToken)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (!await confirmationPrompt
                .PromptForConfirmationAsync($"Delete campaign {campaignId:D}?", cancellationToken)
                .ConfigureAwait(false))
        {
            dispatcher.WriteDiagnostic("Campaign deletion cancelled.");

            return 0;
        }

        Result result = await apiClient.DeleteCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Campaign removed.")));

        return 0;

    }

    /// <summary>
    /// Export a campaign's spells and prompts as JSON (POST /api/campaigns/{id}/export).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    /// <param name="output">Write exported JSON to this file instead of stdout.</param>
    public async Task<int> Export(string? id, string? output = null, CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        Result<CampaignExportDto> result = await apiClient.ExportCampaignAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
            try
            {
                await File.WriteAllTextAsync(output, json, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Could not write '{output}': {ex.Message}")));

                return 1;
            }

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
    public async Task<int> Import(string? id, string? file = null, CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (string.IsNullOrWhiteSpace(file))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--file is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        string json;

        try
        {
            json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Could not read file '{file}': {ex.Message}")));

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
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid campaign export JSON: {ex.Message}")));

            return 1;
        }

        if (payload is null)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("Campaign export JSON parsed to an empty payload.")));

            return (int)CliExitCode.ConfigurationError;
        }

        CampaignImportRequest request = new("merge", payload);

        Result<CampaignImportResultDto> result = await apiClient.ImportCampaignAsync(campaignId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
    public async Task<int> Spells(
        string? id,
        string? query = null,
        string? tag = null,
        string? tool = null,
        CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        Result<SpellSummary[]> result = await apiClient
            .GetCampaignSpellsAsync(campaignId, query, tag, tool, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
    public async Task<int> Prompts(
        string? id,
        string? query = null,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        Result<ListPageResult<PromptSummaryDto>> result = await apiClient
            .GetCampaignPromptsAsync(campaignId, query, tag, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        PromptSummaryDto[] prompts = result.Value.Items;

        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(prompts, ArcanumJsonContext.Default.PromptSummaryDtoArray);

            return 0;

        }

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
    public async Task<int> Sessions(
        string? id,
        string? status = null,
        string? search = null,
        int? limit = null,
        string? beforeUpdatedAt = null,
        CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        DateTimeOffset? beforeUpdatedAtParsed = null;

        if (!string.IsNullOrWhiteSpace(beforeUpdatedAt))
        {

            if (!DateTimeOffset.TryParse(
                    beforeUpdatedAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--before-updated-at must be a valid timestamp.")));

                return (int)CliExitCode.ConfigurationError;
            }

            beforeUpdatedAtParsed = parsed;

        }

        Result<SessionQueryResult> result = await apiClient
            .GetCampaignSessionsAsync(campaignId, status, search, limit, beforeUpdatedAtParsed, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        SessionSummaryDto[] sessions = result.Value.Summaries;

        // The host is allowed to report more rows with no cursor and no rows: hasMore is computed
        // before a tie group is reloaded, and the reload comes back empty if those sessions were
        // archived or deleted in between. That is a host fault in either output mode, so it is
        // decided before the mode is — the same typed no-progress fault the workspace and
        // session-workspace consumers already raise.
        if (result.Value is { HasMore: true, NextBeforeUpdatedAt: null } && sessions.Length == 0)
        {

            CliErrorOutput.WriteMarkupLine(
                themePalette.ErrorMarkup(
                    Markup.Escape(
                        "Api.PaginationNoProgress: the host reported more sessions without "
                        + "an advancing cursor. Re-run the command.")));

            return 1;

        }

        // The paging advice below is operator prose about how to ask for the next page, which a
        // caller reading a document does not need: it already holds every summary, and the cursor it
        // would page from is UpdatedAt on the last one.
        if (CliInvocationContext.Current.Json)
        {

            dispatcher.WriteJson(sessions, ArcanumJsonContext.Default.SessionSummaryDtoArray);

            return 0;

        }

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

            // The empty no-cursor page is already refused above, so the last summary is here to page
            // from whenever the host did not name a cursor of its own.
            DateTimeOffset cursor = result.Value.NextBeforeUpdatedAt ?? sessions[^1].UpdatedAt;

            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape($"More results available \u2014 use --before-updated-at {cursor:O} to page")));

        }

        return 0;

    }

}

/// <summary>
/// Manage the campaign's CODEX.md scratchpad.
/// </summary>
public sealed class CampaignCodexCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConfirmationPrompt confirmationPrompt,
    IOptions<ArcanumSettings> settings,
    ICliResourceCatalog? resourceCatalog = null)
{

    private void WriteError(Error error) =>
        CliErrorOutput.WriteMarkupLine(
            themePalette.ErrorMarkup(CliFailureExit.Annotate(error, settings.Value.Host)));

    /// <summary>
    /// Print CODEX.md (GET /api/campaigns/{id}/codex).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    public async Task<int> Get(string? id, CancellationToken cancellationToken)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        Result<CodexContentDto> result = await apiClient.GetCampaignCodexAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
    public async Task<int> Put(string? id, string? file = null, CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (string.IsNullOrWhiteSpace(file))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--file is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (!CliArgReader.TryReadInlineOrFile($"@{file}", out string content, out string? readError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(readError!)));

            return 1;
        }

        Result<CodexContentDto> result = await apiClient.PutCampaignCodexAsync(campaignId, content, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md updated.")));

        return 0;

    }

    /// <summary>
    /// Delete CODEX.md (DELETE /api/campaigns/{id}/codex).
    /// </summary>
    /// <param name="id">Campaign GUID.</param>
    public async Task<int> Delete(string? id, CancellationToken cancellationToken)
    {

        (bool resolved, bool cancelled, Guid campaignId) = await CampaignCommandSupport.ResolveCampaignIdAsync(id, resourceCatalog, themePalette, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (!await confirmationPrompt
                .PromptForConfirmationAsync($"Delete CODEX.md for campaign {campaignId:D}?", cancellationToken)
                .ConfigureAwait(false))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md deletion cancelled.")));

            return 0;
        }

        Result result = await apiClient.DeleteCampaignCodexAsync(campaignId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md removed.")));

        return 0;

    }

}
