using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Intelligence.Spells;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Tower;

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

/// <summary>
/// Spell utilities (requires arcanum serve).
/// </summary>
public sealed class SpellCommands(
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
    /// List spells (GET /api/spells).
    /// </summary>
    /// <param name="workspace">Workspace root to scope the search (defaults to the host's default workspace).</param>
    public async Task<int> List(string? workspace = null, CancellationToken cancellationToken = default)
    {

        Result<SpellSummary[]> result = await apiClient.GetSpellsAsync(workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        SpellCommandSupport.WriteSpellSummaryTable(result.Value, themePalette);

        return 0;

    }

    /// <summary>
    /// Show spell detail (GET /api/spells/{name}).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace ID, name, or server path used to scope the lookup.</param>
    public async Task<int> Get(string? name, string? workspace = null, CancellationToken cancellationToken = default)
    {

        if (resourceCatalog is not null
            && !string.IsNullOrWhiteSpace(workspace))
        {

            ResourceSelectionResult<WorkspaceInfo> workspaceSelection =
                await resourceCatalog
                    .SelectWorkspaceAsync(workspace, cancellationToken)
                    .ConfigureAwait(false);

            if (workspaceSelection.Status == ResourceSelectionStatus.Cancelled)
            {

                return 0;

            }

            if (workspaceSelection.Status == ResourceSelectionStatus.Error)
            {

                string message = string.IsNullOrWhiteSpace(workspaceSelection.Error)
                    ? "Workspace selection failed."
                    : workspaceSelection.Error;

                CliErrorOutput.WriteMarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape(message)));

                return 1;

            }

            workspace = workspaceSelection.Value!.Path;

        }

        if (resourceCatalog is not null)
        {
            ResourceSelectionResult<SpellSummary> selection = await resourceCatalog
                .SelectSpellAsync(name, workspace, cancellationToken)
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

            name = selection.Value!.Name;
        }
        else if (string.IsNullOrWhiteSpace(name))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("<NAME> is required.")));
            return 1;
        }

        Result<SpellDetail> result = await apiClient.GetSpellAsync(name!, workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
            : body.Length > bodyPreviewChars
                ? body[..Utf8Truncation.SafeCharSliceLength(body, bodyPreviewChars)] + "\u2026"
                : body;

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

    /// <summary>
    /// Create a spell (POST /api/spells).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to create the spell in.</param>
    /// <param name="description">Spell description.</param>
    /// <param name="body">Spell body: inline text, or @filename to read from a file.</param>
    /// <param name="tag">Tag; pass multiple times for several tags.</param>
    /// <param name="declaredTool">Restrict the spell's MCP toolset to these tools (writes SPELL.json); pass multiple times for several tools.</param>
    /// <param name="dependency">Resonant spell dependency name (writes SPELL.json); pass multiple times for several dependencies.</param>
    public async Task<int> Create(
        string? name = null,
        string? workspace = null,
        string? description = null,
        string? body = null,
        string[]? tag = null,
        string[]? declaredTool = null,
        string[]? dependency = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(name))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--name is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (string.IsNullOrWhiteSpace(workspace))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--workspace is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        string resolvedBody = string.Empty;

        if (!string.IsNullOrEmpty(body)
            && !CliArgReader.TryReadInlineOrFile(body, out resolvedBody, out string? bodyError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(bodyError!)));

            return 1;
        }

        CreateSpellRequest request = new(
            name.Trim(),
            description,
            tag ?? [],
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: [],
            RequiredMcpServers: [],
            Body: string.IsNullOrEmpty(resolvedBody) ? null : resolvedBody,
            DeclaredTools: declaredTool,
            Dependencies: dependency);

        Result<bool> result = await apiClient.CreateSpellAsync(request, workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Spell created:"), Markup.Escape(name.Trim())));

        return 0;

    }

    /// <summary>
    /// Update a spell (PUT /api/spells/{name}).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the update.</param>
    /// <param name="description">Spell description.</param>
    /// <param name="tag">Tag; pass multiple times for several tags.</param>
    public async Task<int> Update(
        string name,
        string? workspace = null,
        string? description = null,
        string[]? tag = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(workspace))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--workspace is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        UpdateSpellRequest request = new(
            description,
            tag,
            SystemPrompt: null,
            Template: null,
            Model: null,
            Provider: null,
            Tools: null,
            RequiredMcpServers: null);

        Result<bool> result = await apiClient.UpdateSpellAsync(name, request, workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(themePalette.HighlightLabelMarkup(Markup.Escape("Spell updated:"), Markup.Escape(name)));

        return 0;

    }

    /// <summary>
    /// Delete a spell (DELETE /api/spells/{name}).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the deletion.</param>
    public async Task<int> Delete(string name, string? workspace = null, CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(workspace))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--workspace is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (!await confirmationPrompt
                .PromptForConfirmationAsync($"Delete spell '{name}'?", cancellationToken)
                .ConfigureAwait(false))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.MutedMarkup(Markup.Escape("Spell deletion cancelled.")));

            return 0;
        }

        Result result = await apiClient.DeleteSpellAsync(name, workspace, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Spell removed.")));

        return 0;

    }

    /// <summary>
    /// Search spells by query/tag/tool/source (GET /api/spells/search).
    /// </summary>
    /// <param name="query">-q, Free-text query.</param>
    /// <param name="tag">Filter by tag.</param>
    /// <param name="tool">Filter by declared tool.</param>
    /// <param name="source">Filter by source: builtin, workspace, campaign.</param>
    /// <param name="workspace">Workspace root to scope the search.</param>
    public async Task<int> Search(
        string? query = null,
        string? tag = null,
        string? tool = null,
        string? source = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        SpellSource? parsedSource = null;

        if (!string.IsNullOrWhiteSpace(source))
        {

            parsedSource = source.Trim().ToLowerInvariant() switch
            {
                "builtin" => SpellSource.Builtin,
                "workspace" => SpellSource.Workspace,
                "campaign" => SpellSource.Campaign,
                _ => (SpellSource?)null,
            };

            if (parsedSource is null)
            {
                CliErrorOutput.WriteMarkupLine(
                    themePalette.ErrorMarkup(Markup.Escape("--source must be one of: builtin, workspace, campaign.")));

                return (int)CliExitCode.ConfigurationError;
            }

        }

        Result<SpellSummary[]> result = await apiClient
            .SearchSpellsAsync(query, tag, tool, parsedSource, null, workspace, cancellationToken)
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
    /// Validate a spell's frontmatter and dependencies (POST /api/spells/{name}/validate).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the validation.</param>
    public async Task<int> Validate(string name, string? workspace = null, CancellationToken cancellationToken = default)
    {

        Result<SpellValidationResultDto> result = await apiClient
            .ValidateSpellAsync(name, workspace, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Validate: {name}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = validation.IsValid ? themePalette.HighlightStyle() : themePalette.ErrorStyle(),
        };

        AnsiConsole.Write(panel);

        return 0;

    }

    /// <summary>
    /// Execute a spell and print the assistant response (POST /api/spells/{name}/execute).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the execution.</param>
    /// <param name="version">Spell version label to execute.</param>
    /// <param name="input">Input text for the spell: inline text, or @filename to read from a file.</param>
    public async Task<int> Execute(
        string name,
        string? workspace = null,
        string? version = null,
        string? input = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrEmpty(input))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--input is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (!CliArgReader.TryReadInlineOrFile(input, out string resolvedInput, out string? inputError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(inputError!)));

            return 1;
        }

        SpellExecuteRequest request = new(resolvedInput);

        Result<PromptResponseDto> result = await apiClient
            .ExecuteSpellAsync(name, request, workspace, version, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        await ExecuteResultRendering.WriteExecuteResultAsync(result.Value, themePalette).ConfigureAwait(false);

        return 0;

    }

    /// <summary>
    /// List spell versions (GET /api/spells/{name}/versions).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the lookup.</param>
    public async Task<int> Versions(string name, string? workspace = null, CancellationToken cancellationToken = default)
    {

        Result<SpellVersionDto[]> result = await apiClient
            .GetSpellVersionsAsync(name, workspace, null, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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

    /// <summary>
    /// Export a spell as portable JSON (POST /api/spells/{name}/export).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the export.</param>
    /// <param name="output">Write exported JSON to this file instead of stdout.</param>
    public async Task<int> Export(
        string name,
        string? workspace = null,
        string? output = null,
        CancellationToken cancellationToken = default)
    {

        Result<SpellExportDto> result = await apiClient
            .ExportSpellAsync(name, workspace, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        string json = JsonSerializer.Serialize(
            result.Value,
            RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.SpellExportDto);

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
                themePalette.HighlightLabelMarkup(Markup.Escape("Spell exported to:"), Markup.Escape(output)));
        }

        return 0;

    }

    /// <summary>
    /// Import a spell from portable JSON (POST /api/spells/import).
    /// </summary>
    /// <param name="file">Path to a spell export JSON file.</param>
    /// <param name="workspace">Workspace root to import into.</param>
    public async Task<int> Import(string? file = null, string? workspace = null, CancellationToken cancellationToken = default)
    {

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

        SpellExportDto? payload;

        try
        {
            payload = JsonSerializer.Deserialize(json, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.SpellExportDto);
        }
        catch (JsonException ex)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid spell export JSON: {ex.Message}")));

            return 1;
        }

        if (payload is null)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("Spell export JSON parsed to an empty payload.")));

            return (int)CliExitCode.ConfigurationError;
        }

        SpellImportRequest request = new(payload, workspace, null);

        Result<SpellSummary> result = await apiClient.ImportSpellAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(Markup.Escape("Spell imported:"), Markup.Escape(result.Value.Name)));

        return 0;

    }

    /// <summary>
    /// Dry-run preview of a spell's assembled system prompt — no inference tokens consumed (POST /api/spells/{name}/cast).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="workspace">Workspace root to scope the cast.</param>
    /// <param name="session">Session GUID to bind context from.</param>
    /// <param name="campaign">Campaign GUID to bind context from.</param>
    public async Task<int> Cast(
        string name,
        string? workspace = null,
        string? session = null,
        string? campaign = null,
        CancellationToken cancellationToken = default)
    {

        Guid? sessionId = null;

        if (!string.IsNullOrWhiteSpace(session))
        {

            if (!CliArgReader.TryParseGuid(session, out Guid parsedSessionId))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--session must be a valid GUID.")));

                return (int)CliExitCode.ConfigurationError;
            }

            sessionId = parsedSessionId;

        }

        Guid? campaignId = null;

        if (!string.IsNullOrWhiteSpace(campaign))
        {

            if (!CliArgReader.TryParseGuid(campaign, out Guid parsedCampaignId))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaign must be a valid GUID.")));

                return (int)CliExitCode.ConfigurationError;
            }

            campaignId = parsedCampaignId;

        }

        SpellCastRequest request = new(workspace, sessionId, campaignId);

        Result<SpellCastResult> result = await apiClient.CastSpellAsync(name, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
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

    /// <summary>
    /// Clone a spell to a new name (POST /api/spells/{name}/clone).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="newName">New spell name.</param>
    /// <param name="workspace">Workspace root to scope the clone.</param>
    public async Task<int> Clone(
        string name,
        string? newName = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(newName))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--new-name is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        CloneSpellRequest request = new(newName.Trim(), workspace);

        Result<SpellSummary> result = await apiClient.CloneSpellAsync(name, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Cloned spell \"{name}\" \u2192 \"{result.Value.Name}\"")));

        SpellCommandSupport.WriteSpellSummaryTable([result.Value], themePalette);

        return 0;

    }

}

/// <summary>
/// Manage named spell file versions (SPELL.v{label}.md sidecar files).
/// </summary>
public sealed class SpellVersionCommands(ArcanumApiClient apiClient, IThemePalette themePalette, IOptions<ArcanumSettings> settings)
{

    private void WriteError(Error error) =>
        CliErrorOutput.WriteMarkupLine(
            themePalette.ErrorMarkup(CliFailureExit.Annotate(error, settings.Value.Host)));

    /// <summary>
    /// Create a new spell version (POST /api/spells/{name}/versions).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="version">Version label.</param>
    /// <param name="body">Version body: inline text, or @filename to read from a file.</param>
    /// <param name="workspace">Workspace root to scope the version.</param>
    public async Task<int> Create(
        string name,
        string? version = null,
        string? body = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(version))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (string.IsNullOrEmpty(body))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--body is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (!CliArgReader.TryReadInlineOrFile(body, out string resolvedBody, out string? bodyError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(bodyError!)));

            return 1;
        }

        CreateSpellVersionRequest request = new(version.Trim(), resolvedBody, workspace);

        Result<SpellVersionDto> result = await apiClient.CreateSpellVersionAsync(name, request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Created version {result.Value.Version} for spell \"{name}\"")));

        return 0;

    }

    /// <summary>
    /// Update an existing spell version's body (PUT /api/spells/{name}/versions/{version}).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="version">Version label.</param>
    /// <param name="body">Version body: inline text, or @filename to read from a file.</param>
    /// <param name="workspace">Workspace root to scope the version.</param>
    public async Task<int> Update(
        string name,
        string? version = null,
        string? body = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(version))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (string.IsNullOrEmpty(body))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--body is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        if (!CliArgReader.TryReadInlineOrFile(body, out string resolvedBody, out string? bodyError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(bodyError!)));

            return 1;
        }

        UpdateSpellVersionRequest request = new(resolvedBody, workspace);

        Result<SpellVersionDto> result = await apiClient
            .UpdateSpellVersionAsync(name, version.Trim(), request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Updated version {result.Value.Version} for spell \"{name}\"")));

        return 0;

    }

    /// <summary>
    /// Activate a spell version, swapping it into SPELL.md (POST /api/spells/{name}/versions/{version}/activate).
    /// </summary>
    /// <param name="name">Spell name.</param>
    /// <param name="version">Version label.</param>
    /// <param name="workspace">Workspace root to scope the version.</param>
    public async Task<int> Activate(
        string name,
        string? version = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(version))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--version is required.")));

            return (int)CliExitCode.ConfigurationError;
        }

        ActivateSpellVersionRequest request = new(workspace);

        Result<SpellVersionDto> result = await apiClient
            .ActivateSpellVersionAsync(name, version.Trim(), request, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            WriteError(result.Error);

            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightMarkup(Markup.Escape($"\u2713 Activated version {result.Value.Version} for spell \"{name}\"")));

        if (result.Value.PreviousVersion is not null)
        {
            AnsiConsole.MarkupLine(
                themePalette.MutedMarkup(
                    Markup.Escape($"Previous SPELL.md preserved as SPELL.v{result.Value.PreviousVersion}.md")));
        }

        return 0;

    }

}
