using System.ComponentModel;
using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

public sealed class PromptListCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptListCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.CampaignId))
        {

            if (!CliArgReader.TryParseGuid(settings.CampaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            campaignId = parsed;

        }

        Result<ListPageResult<PromptSummaryDto>> result = await apiClient
            .GetPromptsAsync(campaignId, settings.Query, settings.Tag, cancellationToken: cancellationToken)
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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--campaignId <ID>")]
        public string? CampaignId { get; init; }

        [CommandOption("-q|--query <QUERY>")]
        public string? Query { get; init; }

        [CommandOption("--tag <TAG>")]
        public string? Tag { get; init; }

    }

}

public sealed class PromptGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<PromptDetailDto> result = await apiClient.GetPromptAsync(id, cancellationToken).ConfigureAwait(false);

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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class PromptVersionsCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptVersionsCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.CampaignId))
        {

            if (!CliArgReader.TryParseGuid(settings.CampaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            campaignId = parsed;

        }

        Result<PromptVersionDto[]> result = await apiClient
            .GetPromptVersionsByNameAsync(settings.Name, campaignId, cancellationToken)
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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--campaignId <ID>")]
        public string? CampaignId { get; init; }

    }

}

public sealed class PromptCreateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptCreateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Version))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return 1;
        }

        if (string.IsNullOrEmpty(settings.Template))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--template is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(settings.Template, out string template, out string? templateError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(templateError!)));

            return 1;
        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.CampaignId))
        {

            if (!CliArgReader.TryParseGuid(settings.CampaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            campaignId = parsed;

        }

        CreatePromptRequest request = new(
            settings.Name.Trim(),
            settings.Version.Trim(),
            template,
            settings.Description,
            settings.Tags,
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null,
            campaignId);

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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--name <NAME>")]
        public string? Name { get; init; }

        [CommandOption("--version <VERSION>")]
        public string? Version { get; init; }

        [CommandOption("--template <TEXT_OR_FILE>")]
        [Description("Prompt template: inline text, or @filename to read from a file.")]
        public string? Template { get; init; }

        [CommandOption("--campaignId <ID>")]
        public string? CampaignId { get; init; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("--tag <TAG>")]
        public string[]? Tags { get; init; }

    }

}

public sealed class PromptUpdateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptUpdateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        string? template = null;

        if (!string.IsNullOrEmpty(settings.Template))
        {

            if (!CliArgReader.TryReadInlineOrFile(settings.Template, out string readTemplate, out string? templateError))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(templateError!)));

                return 1;
            }

            template = readTemplate;

        }

        UpdatePromptRequest request = new(
            Name: null,
            Version: null,
            Description: null,
            Tags: settings.Tags,
            Template: template,
            ParameterSchema: null,
            DefaultParameters: null,
            Model: null,
            Provider: null,
            Temperature: null,
            TopP: null,
            MaxOutputTokens: null);

        Result<PromptDetailDto> result = await apiClient.UpdatePromptAsync(id, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Prompt updated:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--template <TEXT_OR_FILE>")]
        public string? Template { get; init; }

        [CommandOption("--tag <TAG>")]
        public string[]? Tags { get; init; }

    }

}

public sealed class PromptDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptDeleteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeletePromptAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Prompt removed.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class PromptRenderCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptRenderCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (!CliArgReader.TryParseKeyValuePairs(settings.Param, out Dictionary<string, string> parameters, out string? paramError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(paramError!)));

            return 1;
        }

        Result<PromptRenderResultDto> result = await apiClient
            .RenderPromptAsync(id, parameters.Count == 0 ? null : parameters, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        await Console.Out.WriteLineAsync(result.Value.RenderedText).ConfigureAwait(false);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--param <KEY=VALUE>")]
        [Description("Template parameter as key=value; pass multiple times for several parameters.")]
        public string[]? Param { get; init; }

    }

}

public sealed class PromptTestCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptTestCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
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

        Result<PromptTestResultDto> result = await apiClient.TestPromptAsync(id, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        await Console.Out.WriteLineAsync(result.Value.AssembledText).ConfigureAwait(false);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class PromptExecuteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptExecuteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrEmpty(settings.Input))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--input is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(settings.Input, out string input, out string? inputError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(inputError!)));

            return 1;
        }

        if (!CliArgReader.TryParseKeyValuePairs(settings.Param, out Dictionary<string, string> parameters, out string? paramError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(paramError!)));

            return 1;
        }

        Guid? sessionId = null;

        if (!string.IsNullOrWhiteSpace(settings.SessionId))
        {

            if (!CliArgReader.TryParseGuid(settings.SessionId, out Guid parsedSessionId))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--sessionId must be a valid GUID.")));

                return 1;
            }

            sessionId = parsedSessionId;

        }

        PromptExecuteRequest request = new(
            input,
            parameters.Count == 0 ? null : parameters,
            SessionId: sessionId);

        Result<PromptResponseDto> result = await apiClient.ExecutePromptAsync(id, request, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        await ForgeExecuteRendering.WriteExecuteResultAsync(result.Value, themePalette).ConfigureAwait(false);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--input <TEXT_OR_FILE>")]
        [Description("User message for the prompt turn: inline text, or @filename to read from a file.")]
        public string? Input { get; init; }

        [CommandOption("--param <KEY=VALUE>")]
        public string[]? Param { get; init; }

        [CommandOption("--sessionId <ID>")]
        public string? SessionId { get; init; }

    }

}

public sealed class PromptCloneCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptCloneCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.NewName) || string.IsNullOrWhiteSpace(settings.NewVersion))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--new-name and --new-version are required.")));

            return 1;
        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.CampaignId))
        {

            if (!CliArgReader.TryParseGuid(settings.CampaignId, out Guid parsedCampaignId))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaign must be a valid GUID.")));

                return 1;
            }

            campaignId = parsedCampaignId;

        }

        ClonePromptRequest request = new(settings.NewName.Trim(), settings.NewVersion.Trim(), campaignId);

        Result<PromptDetailDto> result = await apiClient.ClonePromptAsync(id, request, cancellationToken).ConfigureAwait(false);

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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--new-name <NAME>")]
        public required string NewName { get; init; }

        [CommandOption("--new-version <VERSION>")]
        public required string NewVersion { get; init; }

        [CommandOption("--campaign <ID>")]
        public string? CampaignId { get; init; }

    }

}

public sealed class PromptExportCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptExportCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<PromptExportDto> result = await apiClient.ExportPromptAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        string json = JsonSerializer.Serialize(
            result.Value,
            RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.PromptExportDto);

        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(settings.Output, json, cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                themePalette.HighlightLabelMarkup(Markup.Escape("Prompt exported to:"), Markup.Escape(settings.Output)));
        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--output <FILE>")]
        public string? Output { get; init; }

    }

}

public sealed class PromptImportCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<PromptImportCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

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

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(settings.CampaignId))
        {

            if (!CliArgReader.TryParseGuid(settings.CampaignId, out Guid parsed))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            campaignId = parsed;

        }

        PromptImportRequest request = new(payload, campaignId);

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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--file <FILE>")]
        public string? File { get; init; }

        [CommandOption("--campaignId <ID>")]
        public string? CampaignId { get; init; }

    }

}
