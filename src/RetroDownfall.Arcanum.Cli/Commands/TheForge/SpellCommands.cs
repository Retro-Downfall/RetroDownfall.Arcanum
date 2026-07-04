using System.ComponentModel;
using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

internal static class SpellCommandSupport
{

    public static void WriteSpellSummaryTable(SpellSummary[] spells, IThemePalette themePalette)
    {

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Name")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Description")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Source")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Tags")));

        foreach (SpellSummary spell in spells)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(spell.Name))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(spell.Description ?? "-"))),
                new Markup(themePalette.TextMarkup(Markup.Escape(spell.Source.ToString()))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(spell.Tags.Length == 0 ? "-" : string.Join(", ", spell.Tags)))));

        }

        AnsiConsole.Write(table);

        if (spells.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No spells matched.")));
        }

    }

}

public sealed class SpellListCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellListCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<SpellSummary[]> result = await apiClient.GetSpellsAsync(settings.Workspace, cancellationToken).ConfigureAwait(false);

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

        [CommandOption("--workspace <PATH>")]
        [Description("Workspace root to scope the search (defaults to the host's default workspace).")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<SpellDetail> result = await apiClient.GetSpellAsync(settings.Name, settings.Workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        SpellDetail spell = result.Value;

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Name:")), themePalette.HighlightMarkup(Markup.Escape(spell.Name)));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Description:")),
            themePalette.TextMarkup(Markup.Escape(spell.Description ?? "(none)")));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Source:")), themePalette.TextMarkup(Markup.Escape(spell.Source.ToString())));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Tags:")),
            themePalette.TextMarkup(Markup.Escape(spell.Tags.Length == 0 ? "(none)" : string.Join(", ", spell.Tags))));

        if (spell.DeclaredTools is { Length: > 0 } declaredTools)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Declared tools:")),
                themePalette.TextMarkup(Markup.Escape(string.Join(", ", declaredTools))));
        }

        if (spell.Dependencies is { Length: > 0 } dependencies)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Dependencies:")),
                themePalette.TextMarkup(Markup.Escape(string.Join(", ", dependencies))));
        }

        const int bodyPreviewChars = 800;

        string? body = spell.Body;

        string bodyPreview = string.IsNullOrEmpty(body)
            ? "(empty)"
            : body.Length > bodyPreviewChars ? body[..bodyPreviewChars] + "\u2026" : body;

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Body:")), themePalette.TextMarkup(Markup.Escape(bodyPreview)));

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Spell: {spell.Name}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellCreateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellCreateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Name))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Workspace))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--workspace is required.")));

            return 1;
        }

        string body = string.Empty;

        if (!string.IsNullOrEmpty(settings.Body)
            && !CliArgReader.TryReadInlineOrFile(settings.Body, out body, out string? bodyError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(bodyError!)));

            return 1;
        }

        CreateSpellRequest request = new(
            settings.Name.Trim(),
            settings.Description,
            settings.Tags ?? [],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: [],
            Body: string.IsNullOrEmpty(body) ? null : body,
            DeclaredTools: settings.DeclaredTools,
            Dependencies: settings.Dependencies);

        Result<bool> result = await apiClient.CreateSpellAsync(request, settings.Workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Spell created:"), Markup.Escape(settings.Name.Trim())));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--name <NAME>")]
        public string? Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("--body <TEXT_OR_FILE>")]
        [Description("Spell body: inline text, or @filename to read from a file.")]
        public string? Body { get; init; }

        [CommandOption("--tag <TAG>")]
        public string[]? Tags { get; init; }

        [CommandOption("--declared-tool <TOOL>")]
        [Description("Restrict the spell's MCP toolset to these tools (writes SKILL.json).")]
        public string[]? DeclaredTools { get; init; }

        [CommandOption("--dependency <SPELL_NAME>")]
        [Description("Resonant spell dependency name (writes SKILL.json).")]
        public string[]? Dependencies { get; init; }

    }

}

public sealed class SpellUpdateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellUpdateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Workspace))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--workspace is required.")));

            return 1;
        }

        UpdateSpellRequest request = new(
            settings.Description,
            settings.Tags,
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null);

        Result<bool> result = await apiClient.UpdateSpellAsync(settings.Name, request, settings.Workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightLabelMarkup(Markup.Escape("Spell updated:"), Markup.Escape(settings.Name)));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; init; }

        [CommandOption("--tag <TAG>")]
        public string[]? Tags { get; init; }

    }

}

public sealed class SpellDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellDeleteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Workspace))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--workspace is required.")));

            return 1;
        }

        Result result = await apiClient.DeleteSpellAsync(settings.Name, settings.Workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Spell removed.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellSearchCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellSearchCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        SpellSource? source = null;

        if (!string.IsNullOrWhiteSpace(settings.Source))
        {

            source = settings.Source.Trim().ToLowerInvariant() switch
            {
                "builtin" => SpellSource.Builtin,
                "workspace" => SpellSource.Workspace,
                "campaign" => SpellSource.Campaign,
                _ => (SpellSource?)null,
            };

            if (source is null)
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape("--source must be one of: builtin, workspace, campaign.")));

                return 1;
            }

        }

        Result<SpellSummary[]> result = await apiClient
            .SearchSpellsAsync(settings.Query, settings.Tag, settings.Tool, source, null, settings.Workspace, cancellationToken)
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

        [CommandOption("-q|--query <QUERY>")]
        public string? Query { get; init; }

        [CommandOption("--tag <TAG>")]
        public string? Tag { get; init; }

        [CommandOption("--tool <TOOL>")]
        public string? Tool { get; init; }

        [CommandOption("--source <SOURCE>")]
        [Description("Filter by source: builtin, workspace, campaign.")]
        public string? Source { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellValidateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellValidateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<SpellValidationResultDto> result = await apiClient
            .ValidateSpellAsync(settings.Name, settings.Workspace, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        SpellValidationResultDto validation = result.Value;

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        string validText = validation.IsValid ? "Valid" : "Invalid";

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Result:")),
            validation.IsValid
                ? themePalette.HighlightMarkup(Markup.Escape(validText))
                : themePalette.ErrorMarkup(Markup.Escape(validText)));

        if (validation.Errors.Length > 0)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Errors:")),
                themePalette.ErrorMarkup(Markup.Escape(string.Join("; ", validation.Errors))));
        }

        if (validation.Warnings.Length > 0)
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Warnings:")),
                themePalette.TextMarkup(Markup.Escape(string.Join("; ", validation.Warnings))));
        }

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Validate: {settings.Name}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = validation.IsValid ? themePalette.HighlightStyle() : themePalette.ErrorStyle(),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellExecuteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellExecuteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

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

        SpellExecuteRequest request = new(input);

        Result<PromptResponseDto> result = await apiClient
            .ExecuteSpellAsync(settings.Name, request, settings.Workspace, settings.Version, cancellationToken)
            .ConfigureAwait(false);

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

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

        [CommandOption("--version <LABEL>")]
        public string? Version { get; init; }

        [CommandOption("--input <TEXT_OR_FILE>")]
        [Description("Input text for the spell: inline text, or @filename to read from a file.")]
        public string? Input { get; init; }

    }

}

public sealed class SpellVersionsCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellVersionsCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<SpellVersionDto[]> result = await apiClient
            .GetSpellVersionsAsync(settings.Name, settings.Workspace, null, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Version")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Active")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Modified")));

        foreach (SpellVersionDto version in result.Value)
        {

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(version.Version))),
                new Markup(version.IsActive
                    ? themePalette.HighlightMarkup(Markup.Escape("yes"))
                    : themePalette.MutedMarkup(Markup.Escape("-"))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(version.CreatedAt.ToString("u")))));

        }

        AnsiConsole.Write(table);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellExportCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellExportCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Result<SpellExportDto> result = await apiClient
            .ExportSpellAsync(settings.Name, settings.Workspace, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        string json = JsonSerializer.Serialize(
            result.Value,
            RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.SpellExportDto);

        if (string.IsNullOrWhiteSpace(settings.Output))
        {
            await Console.Out.WriteLineAsync(json).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(settings.Output, json, cancellationToken).ConfigureAwait(false);

            AnsiConsole.MarkupLine(
                themePalette.HighlightLabelMarkup(Markup.Escape("Spell exported to:"), Markup.Escape(settings.Output)));
        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

        [CommandOption("--output <FILE>")]
        public string? Output { get; init; }

    }

}

public sealed class SpellImportCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellImportCommand.Settings>
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

        SpellExportDto? payload;

        try
        {
            payload = JsonSerializer.Deserialize(json, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.SpellExportDto);
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid spell export JSON: {ex.Message}")));

            return 1;
        }

        if (payload is null)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("Spell export JSON parsed to an empty payload.")));

            return 1;
        }

        SpellImportRequest request = new(payload, settings.Workspace, null);

        Result<SpellSummary> result = await apiClient.ImportSpellAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Spell imported:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--file <FILE>")]
        public string? File { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellCastCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellCastCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        Guid? sessionId = null;

        if (!string.IsNullOrWhiteSpace(settings.SessionId))
        {

            if (!CliArgReader.TryParseGuid(settings.SessionId, out Guid parsedSessionId))
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--session must be a valid GUID.")));

                return 1;
            }

            sessionId = parsedSessionId;

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

        SpellCastRequest request = new(settings.Workspace, sessionId, campaignId);

        Result<SpellCastResult> result = await apiClient.CastSpellAsync(settings.Name, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        SpellCastResult cast = result.Value;

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Dry-run cast \u2014 no inference tokens consumed.")));

        string header = string.IsNullOrWhiteSpace(cast.SpellDescription)
            ? $"Spell Cast: {cast.SpellName}"
            : $"Spell Cast: {cast.SpellName} \u2014 {cast.SpellDescription}";

        AnsiConsole.MarkupLine(themePalette.HeadingBoldMarkup(Markup.Escape(header)));

        Panel promptPanel = new(Markup.Escape(cast.SystemPrompt))
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape("Assembled System Prompt"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(promptPanel);

        if (cast.ResonantDependencies.Length > 0)
        {

            Table depTable = new();

            depTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Spell Name")));

            foreach (string dependency in cast.ResonantDependencies)
            {
                depTable.AddRow(new Markup(themePalette.TextMarkup(Markup.Escape(dependency))));
            }

            AnsiConsole.MarkupLine(themePalette.HeadingBoldMarkup(Markup.Escape("Resonant Dependencies")));

            AnsiConsole.Write(depTable);

        }

        if (cast.AvailableTools.Length > 0)
        {

            Table toolTable = new();

            toolTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Tool Name")));

            foreach (string tool in cast.AvailableTools)
            {
                toolTable.AddRow(new Markup(themePalette.TextMarkup(Markup.Escape(tool))));
            }

            AnsiConsole.MarkupLine(themePalette.HeadingBoldMarkup(Markup.Escape("Available Tools (Artifact Attunement)")));

            AnsiConsole.Write(toolTable);

            if (!cast.HasDeclaredToolsFilter)
            {
                AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("(all tools available \u2014 no attunement filter)")));
            }

        }

        if (cast.AvailableSpellScripts.Length > 0)
        {

            Table scriptTable = new();

            scriptTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Script")));

            foreach (string script in cast.AvailableSpellScripts)
            {
                scriptTable.AddRow(new Markup(themePalette.TextMarkup(Markup.Escape(script))));
            }

            AnsiConsole.MarkupLine(themePalette.HeadingBoldMarkup(Markup.Escape("Spell Scripts")));

            AnsiConsole.Write(scriptTable);

        }

        if (cast.CodexContent is not null)
        {
            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(Markup.Escape($"Codex: present ({cast.CodexContent.Length} chars)")));
        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

        [CommandOption("--session <ID>")]
        public string? SessionId { get; init; }

        [CommandOption("--campaign <ID>")]
        public string? CampaignId { get; init; }

    }

}

public sealed class SpellCloneCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellCloneCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.NewName))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--new-name is required.")));

            return 1;
        }

        CloneSpellRequest request = new(settings.NewName.Trim(), settings.Workspace);

        Result<SpellSummary> result = await apiClient.CloneSpellAsync(settings.Name, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Cloned spell \"{settings.Name}\" \u2192 \"{result.Value.Name}\"")));

        SpellCommandSupport.WriteSpellSummaryTable([result.Value], themePalette);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--new-name <NAME>")]
        public required string NewName { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellVersionCreateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellVersionCreateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Version))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return 1;
        }

        if (string.IsNullOrEmpty(settings.Body))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--body is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(settings.Body, out string body, out string? bodyError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(bodyError!)));

            return 1;
        }

        CreateSpellVersionRequest request = new(settings.Version.Trim(), body, settings.Workspace);

        Result<SpellVersionDto> result = await apiClient.CreateSpellVersionAsync(settings.Name, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Created version {result.Value.Version} for spell \"{settings.Name}\"")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--version <LABEL>")]
        public string? Version { get; init; }

        [CommandOption("--body <TEXT_OR_FILE>")]
        [Description("Version body: inline text, or @filename to read from a file.")]
        public string? Body { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellVersionUpdateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellVersionUpdateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Version))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return 1;
        }

        if (string.IsNullOrEmpty(settings.Body))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--body is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(settings.Body, out string body, out string? bodyError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(bodyError!)));

            return 1;
        }

        UpdateSpellVersionRequest request = new(body, settings.Workspace);

        Result<SpellVersionDto> result = await apiClient
            .UpdateSpellVersionAsync(settings.Name, settings.Version.Trim(), request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Updated version {result.Value.Version} for spell \"{settings.Name}\"")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--version <LABEL>")]
        public string? Version { get; init; }

        [CommandOption("--body <TEXT_OR_FILE>")]
        [Description("Version body: inline text, or @filename to read from a file.")]
        public string? Body { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class SpellVersionActivateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<SpellVersionActivateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrWhiteSpace(settings.Version))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return 1;
        }

        ActivateSpellVersionRequest request = new(settings.Workspace);

        Result<SpellVersionDto> result = await apiClient
            .ActivateSpellVersionAsync(settings.Name, settings.Version.Trim(), request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Activated version {result.Value.Version} for spell \"{settings.Name}\"")));

        if (result.Value.PreviousVersion is not null)
        {
            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape($"Previous SPELL.md preserved as SPELL.v{result.Value.PreviousVersion}.md")));
        }

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<NAME>")]
        public required string Name { get; init; }

        [CommandOption("--version <LABEL>")]
        public string? Version { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}
