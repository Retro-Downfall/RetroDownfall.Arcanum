using System.ComponentModel;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

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

public sealed class CampaignListCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignListCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        WorkspaceType? type = null;

        if (!string.IsNullOrWhiteSpace(settings.Type))
        {

            if (!CampaignCommandSupport.TryParseWorkspaceType(settings.Type, out WorkspaceType parsed))
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape("--type must be one of: spell, campaign, data, custom.")));

                return 1;
            }

            type = parsed;

        }

        Result<ListPageResult<CampaignDto>> result = await apiClient.GetCampaignsAsync(type, cancellationToken).ConfigureAwait(false);

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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--type <TYPE>")]
        [Description("Filter by workspace type: spell, campaign, data, custom.")]
        public string? Type { get; init; }

    }

}

public sealed class CampaignGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<CampaignDto> result = await apiClient.GetCampaignAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        CampaignCommandSupport.WriteCampaignDetailPanel(result.Value, themePalette);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class CampaignCreateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignCreateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--path is required.")));

            return 1;
        }

        string typeText = string.IsNullOrWhiteSpace(settings.Type) ? "campaign" : settings.Type;

        if (!CampaignCommandSupport.TryParseWorkspaceType(typeText, out WorkspaceType type))
        {
            AnsiConsole.MarkupLine(
                themePalette.ErrorMarkup(Markup.Escape("--type must be one of: spell, campaign, data, custom.")));

            return 1;
        }

        RegisterCampaignRequest request = new(settings.Name.Trim(), settings.Path.Trim(), type, settings.Description);

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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--name <NAME>")]
        [Description("Campaign display name.")]
        public string? Name { get; init; }

        [CommandOption("--path <PATH>")]
        [Description("Absolute filesystem path to register as the campaign root.")]
        public string? Path { get; init; }

        [CommandOption("--type <TYPE>")]
        [Description("Workspace type: spell, campaign, data, custom. Defaults to campaign.")]
        public string? Type { get; init; }

        [CommandOption("--description <TEXT>")]
        [Description("Optional campaign description.")]
        public string? Description { get; init; }

    }

}

public sealed class CampaignUpdateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignUpdateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        UpdateCampaignRequest request = new(settings.Name, null, null, null);

        Result<CampaignDto> result = await apiClient.UpdateCampaignAsync(id, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Campaign updated:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("New campaign display name.")]
        public string? Name { get; init; }

    }

}

public sealed class CampaignDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignDeleteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeleteCampaignAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Campaign removed.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class CampaignExportCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignExportCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<CampaignExportDto> result = await apiClient.ExportCampaignAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        string json = System.Text.Json.JsonSerializer.Serialize(
            result.Value,
            RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.CampaignExportDto);

        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(settings.Output, json, cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                themePalette.HighlightLabelMarkup(Markup.Escape("Campaign exported to:"), Markup.Escape(settings.Output)));
        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--output <FILE>")]
        [Description("Write exported JSON to this file instead of stdout.")]
        public string? Output { get; init; }

    }

}

public sealed class CampaignImportCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignImportCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.File))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--file is required.")));

            return 1;
        }

        string json;

        try
        {
            json = await File.ReadAllTextAsync(settings.File, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Could not read file '{settings.File}': {ex.Message}")));

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

        Result<CampaignImportResultDto> result = await apiClient.ImportCampaignAsync(id, request, cancellationToken).ConfigureAwait(false);

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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--file <FILE>")]
        [Description("Path to a campaign export JSON file (as produced by 'campaign export').")]
        public string? File { get; init; }

    }

}

public sealed class CampaignSpellsCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignSpellsCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<SpellSummary[]> result = await apiClient
            .GetCampaignSpellsAsync(id, settings.Query, settings.Tag, settings.Tool, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        SpellCommandSupport.WriteSpellSummaryTable(result.Value, themePalette);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("-q|--query <QUERY>")]
        public string? Query { get; init; }

        [CommandOption("--tag <TAG>")]
        public string? Tag { get; init; }

        [CommandOption("--tool <TOOL>")]
        public string? Tool { get; init; }

    }

}

public sealed class CampaignPromptsCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignPromptsCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<ListPageResult<PromptSummaryDto>> result = await apiClient
            .GetCampaignPromptsAsync(id, settings.Query, settings.Tag, cancellationToken)
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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("-q|--query <QUERY>")]
        public string? Query { get; init; }

        [CommandOption("--tag <TAG>")]
        public string? Tag { get; init; }

    }

}

public sealed class CampaignSessionsCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignSessionsCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        DateTimeOffset? beforeUpdatedAt = null;

        if (!string.IsNullOrWhiteSpace(settings.BeforeUpdatedAt))
        {

            if (!DateTimeOffset.TryParse(
                    settings.BeforeUpdatedAt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--before-updated-at must be a valid timestamp.")));

                return 1;
            }

            beforeUpdatedAt = parsed;

        }

        Result<SessionQueryResult> result = await apiClient
            .GetCampaignSessionsAsync(id, settings.Status, settings.Search, settings.Limit, beforeUpdatedAt, cancellationToken)
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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--status <STATUS>")]
        public string? Status { get; init; }

        [CommandOption("--search <QUERY>")]
        public string? Search { get; init; }

        [CommandOption("--limit <N>")]
        public int? Limit { get; init; }

        [CommandOption("--before-updated-at <TIMESTAMP>")]
        public string? BeforeUpdatedAt { get; init; }

    }

}

public sealed class CampaignCodexGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignCodexGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<CodexContentDto> result = await apiClient.GetCampaignCodexAsync(id, cancellationToken).ConfigureAwait(false);

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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class CampaignCodexPutCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignCodexPutCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.File))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--file is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile($"@{settings.File}", out string content, out string? readError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(readError!)));

            return 1;
        }

        Result<CodexContentDto> result = await apiClient.PutCampaignCodexAsync(id, content, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md updated.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--file <FILE>")]
        [Description("Path to a file whose contents become CODEX.md.")]
        public string? File { get; init; }

    }

}

public sealed class CampaignCodexDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<CampaignCodexDeleteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeleteCampaignCodexAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("CODEX.md removed.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}
