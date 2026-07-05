using System.Text.Json;
using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

/// <summary>
/// The Forge prompt utilities (requires arcanum serve).
/// </summary>
public sealed class PromptCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// List prompts (GET /api/prompts).
    /// </summary>
    /// <param name="campaignId">--campaignId, Filter by campaign GUID.</param>
    /// <param name="query">-q, Free-text query.</param>
    /// <param name="tag">Filter by tag.</param>
    [Command("list")]
    public async Task<int> List(
        string? campaignId = null,
        string? query = null,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaignId))
        {

            if (!CliArgReader.TryParseGuid(campaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        Result<ListPageResult<PromptSummaryDto>> result = await apiClient
            .GetPromptsAsync(parsedCampaignId, query, tag, cancellationToken: cancellationToken)
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

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Campaign")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Tags")));

        foreach (PromptSummaryDto prompt in prompts)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(prompt.Name))),
                new Markup(themePalette.TextMarkup(Markup.Escape(prompt.Version))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(prompt.CampaignId?.ToString("D") ?? "-"))),
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
    /// Show prompt detail (GET /api/prompts/{id}).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    [Command("get")]
    public async Task<int> Get([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<PromptDetailDto> result = await apiClient.GetPromptAsync(promptId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        PromptDetailDto prompt = result.Value;

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Id:")), themePalette.TextMarkup(Markup.Escape(prompt.Id.ToString("D"))));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Name:")), themePalette.HighlightMarkup(Markup.Escape(prompt.Name)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Version:")), themePalette.TextMarkup(Markup.Escape(prompt.Version)));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Campaign:")),
            themePalette.TextMarkup(Markup.Escape(prompt.CampaignId?.ToString("D") ?? "(none)")));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Description:")),
            themePalette.TextMarkup(Markup.Escape(prompt.Description ?? "(none)")));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Tags:")),
            themePalette.TextMarkup(Markup.Escape(prompt.Tags.Length == 0 ? "(none)" : string.Join(", ", prompt.Tags))));

        const int templatePreviewChars = 800;

        string template = prompt.Template.Length > templatePreviewChars
            ? prompt.Template[..templatePreviewChars] + "\u2026"
            : prompt.Template;

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Template:")), themePalette.TextMarkup(Markup.Escape(template)));

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Prompt: {prompt.Name} v{prompt.Version}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    /// <summary>
    /// List versions of a prompt by name (GET /api/prompts/by-name/{name}/versions).
    /// </summary>
    /// <param name="name">Prompt name.</param>
    /// <param name="campaignId">--campaignId, Filter by campaign GUID.</param>
    [Command("versions")]
    public async Task<int> Versions([Argument] string name, string? campaignId = null, CancellationToken cancellationToken = default)
    {

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaignId))
        {

            if (!CliArgReader.TryParseGuid(campaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        Result<PromptVersionDto[]> result = await apiClient
            .GetPromptVersionsByNameAsync(name, parsedCampaignId, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Version")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Modified")));

        foreach (PromptVersionDto version in result.Value)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(version.Version))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(version.UpdatedAt.ToString("u")))));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    /// <summary>
    /// Create a prompt (POST /api/prompts).
    /// </summary>
    /// <param name="name">Prompt name.</param>
    /// <param name="version">Prompt version label.</param>
    /// <param name="template">Prompt template: inline text, or @filename to read from a file.</param>
    /// <param name="campaignId">--campaignId, Campaign GUID to associate with.</param>
    /// <param name="description">Prompt description.</param>
    /// <param name="tag">Tag; pass multiple times for several tags.</param>
    [Command("create")]
    public async Task<int> Create(
        string? name = null,
        string? version = null,
        string? template = null,
        string? campaignId = null,
        string? description = null,
        string[]? tag = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(name))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return 1;
        }

        if (string.IsNullOrEmpty(template))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--template is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(template, out string resolvedTemplate, out string? templateError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(templateError!)));

            return 1;
        }

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaignId))
        {

            if (!CliArgReader.TryParseGuid(campaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        CreatePromptRequest request = new(
            name.Trim(),
            version.Trim(),
            resolvedTemplate,
            description,
            tag,
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            parsedCampaignId);

        Result<PromptDetailDto> result = await apiClient.CreatePromptAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Prompt created:"),
                Markup.Escape($"{result.Value.Name} v{result.Value.Version} ({result.Value.Id:D})")));

        return 0;

    }

    /// <summary>
    /// Update a prompt (PUT /api/prompts/{id}).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    /// <param name="template">Prompt template: inline text, or @filename to read from a file.</param>
    /// <param name="tag">Tag; pass multiple times for several tags.</param>
    [Command("update")]
    public async Task<int> Update(
        [Argument] string id,
        string? template = null,
        string[]? tag = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        string? resolvedTemplate = null;

        if (!string.IsNullOrEmpty(template))
        {

            if (!CliArgReader.TryReadInlineOrFile(template, out string readTemplate, out string? templateError))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(templateError!)));

                return 1;
            }

            resolvedTemplate = readTemplate;

        }

        UpdatePromptRequest request = new(
            Name: null,
            Version: null,
            Description: null,
            Tags: tag,
            Template: resolvedTemplate,
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null);

        Result<PromptDetailDto> result = await apiClient.UpdatePromptAsync(promptId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Prompt updated:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    /// <summary>
    /// Delete a prompt (DELETE /api/prompts/{id}).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    [Command("delete")]
    public async Task<int> Delete([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeletePromptAsync(promptId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Prompt removed.")));

        return 0;

    }

    /// <summary>
    /// Render a prompt template with parameters (POST /api/prompts/{id}/render).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    /// <param name="param">Template parameter as key=value; pass multiple times for several parameters.</param>
    [Command("render")]
    public async Task<int> Render([Argument] string id, string[]? param = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (!CliArgReader.TryParseKeyValuePairs(param, out Dictionary<string, string> parameters, out string? paramError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(paramError!)));

            return 1;
        }

        Result<PromptRenderResultDto> result = await apiClient
            .RenderPromptAsync(promptId, parameters.Count == 0 ? null : parameters, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        await Console.Out.WriteLineAsync(result.Value.RenderedText).ConfigureAwait(false);

        return 0;

    }

    /// <summary>
    /// Assemble the system prompt without LLM cost (POST /api/prompts/{id}/test).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    [Command("test")]
    public async Task<int> Test([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        TestPromptRequest request = new(
            WorkingDirectory: Environment.CurrentDirectory,
            ContextSnapshot: null,
            ChronosyncDelta: null,
            CodexPath: null,
            AttachedFiles: null);

        Result<PromptTestResultDto> result = await apiClient.TestPromptAsync(promptId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        await Console.Out.WriteLineAsync(result.Value.AssembledText).ConfigureAwait(false);

        return 0;

    }

    /// <summary>
    /// Render and run session-backed inference (POST /api/prompts/{id}/execute).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    /// <param name="input">User message for the prompt turn: inline text, or @filename to read from a file.</param>
    /// <param name="param">Template parameter as key=value; pass multiple times for several parameters.</param>
    /// <param name="sessionId">--sessionId, Session GUID to bind context from.</param>
    [Command("execute")]
    public async Task<int> Execute(
        [Argument] string id,
        string? input = null,
        string[]? param = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrEmpty(input))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--input is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(input, out string resolvedInput, out string? inputError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(inputError!)));

            return 1;
        }

        if (!CliArgReader.TryParseKeyValuePairs(param, out Dictionary<string, string> parameters, out string? paramError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(paramError!)));

            return 1;
        }

        Guid? parsedSessionId = null;

        if (!string.IsNullOrWhiteSpace(sessionId))
        {

            if (!CliArgReader.TryParseGuid(sessionId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--sessionId must be a valid GUID.")));

                return 1;
            }

            parsedSessionId = parsed;

        }

        PromptExecuteRequest request = new(
            resolvedInput,
            parameters.Count == 0 ? null : parameters,
            SessionId: parsedSessionId);

        Result<PromptResponseDto> result = await apiClient.ExecutePromptAsync(promptId, request, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        await ForgeExecuteRendering.WriteExecuteResultAsync(result.Value, themePalette).ConfigureAwait(false);

        return 0;

    }

    /// <summary>
    /// Clone a prompt to a new name/version (POST /api/prompts/{id}/clone).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    /// <param name="newName">New prompt name.</param>
    /// <param name="newVersion">New prompt version label.</param>
    /// <param name="campaign">Campaign GUID to associate the clone with.</param>
    [Command("clone")]
    public async Task<int> Clone(
        [Argument] string id,
        string? newName = null,
        string? newVersion = null,
        string? campaign = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(newName) || string.IsNullOrWhiteSpace(newVersion))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--new-name and --new-version are required.")));

            return 1;
        }

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaign))
        {

            if (!CliArgReader.TryParseGuid(campaign, out Guid parsedCampaignId2))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaign must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsedCampaignId2;

        }

        ClonePromptRequest request = new(newName.Trim(), newVersion.Trim(), parsedCampaignId);

        Result<PromptDetailDto> result = await apiClient.ClonePromptAsync(promptId, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        PromptDetailDto prompt = result.Value;

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Cloned prompt \u2192 \"{prompt.Name}\" v{prompt.Version}")));

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Name:")), themePalette.HighlightMarkup(Markup.Escape(prompt.Name)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Version:")), themePalette.TextMarkup(Markup.Escape(prompt.Version)));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Campaign:")),
            themePalette.TextMarkup(Markup.Escape(prompt.CampaignId?.ToString("D") ?? "(none)")));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Tags:")),
            themePalette.TextMarkup(Markup.Escape(prompt.Tags.Length == 0 ? "(none)" : string.Join(", ", prompt.Tags))));

        const int templatePreviewChars = 200;

        string template = prompt.Template.Length > templatePreviewChars
            ? prompt.Template[..templatePreviewChars] + "\u2026"
            : prompt.Template;

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Template:")), themePalette.TextMarkup(Markup.Escape(template)));

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Prompt: {prompt.Name} v{prompt.Version}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    /// <summary>
    /// Export a prompt as portable JSON (POST /api/prompts/{id}/export).
    /// </summary>
    /// <param name="id">Prompt GUID.</param>
    /// <param name="output">Write exported JSON to this file instead of stdout.</param>
    [Command("export")]
    public async Task<int> Export([Argument] string id, string? output = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid promptId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<PromptExportDto> result = await apiClient.ExportPromptAsync(promptId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        string json = JsonSerializer.Serialize(
            result.Value,
            RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.PromptExportDto);

        if (string.IsNullOrWhiteSpace(output))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(output, json, cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                themePalette.HighlightLabelMarkup(Markup.Escape("Prompt exported to:"), Markup.Escape(output)));
        }

        return 0;

    }

    /// <summary>
    /// Import a prompt from portable JSON (POST /api/prompts/import).
    /// </summary>
    /// <param name="file">Path to a prompt export JSON file.</param>
    /// <param name="campaignId">--campaignId, Campaign GUID to associate the import with.</param>
    [Command("import")]
    public async Task<int> Import(string? file = null, string? campaignId = null, CancellationToken cancellationToken = default)
    {

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

        PromptExportDto? payload;

        try
        {
            payload = JsonSerializer.Deserialize(json, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.PromptExportDto);
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid prompt export JSON: {ex.Message}")));

            return 1;
        }

        if (payload is null)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Prompt export JSON parsed to an empty payload.")));

            return 1;
        }

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaignId))
        {

            if (!CliArgReader.TryParseGuid(campaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        PromptImportRequest request = new(payload, parsedCampaignId);

        Result<PromptSummaryDto> result = await apiClient.ImportPromptAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Prompt imported:"),
                Markup.Escape($"{result.Value.Name} v{result.Value.Version}")));

        return 0;

    }

}
