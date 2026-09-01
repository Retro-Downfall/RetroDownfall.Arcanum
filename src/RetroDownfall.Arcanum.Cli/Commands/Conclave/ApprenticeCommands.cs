using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Commands;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.Conclave;

internal static class ApprenticeCommandSupport
{

    private const int MaxDerivedNameChars = 60;

    public static string DeriveNameFromGoal(string goal)
    {

        string trimmed = goal.Trim();

        return trimmed.Length > MaxDerivedNameChars ? trimmed[..MaxDerivedNameChars] + "\u2026" : trimmed;

    }

    public static void WriteApprenticeDetailPanel(ApprenticeDetailDto apprentice, IThemePalette themePalette)
    {

        Table table = new();

        table.Border(TableBorder.None);

        table.HideHeaders();

        table.AddColumn(new TableColumn(string.Empty).NoWrap());

        table.AddColumn(new TableColumn(string.Empty));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Id:")), themePalette.TextMarkup(Markup.Escape(apprentice.Id.ToString("D"))));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Name:")), themePalette.HighlightMarkup(Markup.Escape(apprentice.Name)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Goal:")), themePalette.TextMarkup(Markup.Escape(apprentice.Goal)));

        table.AddRow(themePalette.MutedMarkup(Markup.Escape("Status:")), themePalette.TextMarkup(Markup.Escape(apprentice.Status)));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Current step:")),
            themePalette.TextMarkup(Markup.Escape($"{apprentice.CurrentStep}/{apprentice.Plan.Count}")));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Campaign:")),
            themePalette.TextMarkup(Markup.Escape(apprentice.CampaignId?.ToString("D") ?? "(none)")));

        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape("Workspace:")),
            themePalette.TextMarkup(Markup.Escape(apprentice.WorkspacePath)));

        if (!string.IsNullOrWhiteSpace(apprentice.ErrorMessage))
        {
            table.AddRow(
                themePalette.MutedMarkup(Markup.Escape("Error:")),
                themePalette.ErrorMarkup(Markup.Escape(apprentice.ErrorMessage)));
        }

        Panel panel = new(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup(Markup.Escape($"Apprentice: {apprentice.Name}"))),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        };

        AnsiConsole.Write(panel);

        if (apprentice.Plan.Count == 0)
        {
            return;
        }

        Table planTable = new();

        planTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Step")));

        planTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Description")));

        planTable.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Status")));

        foreach (PlanStep step in apprentice.Plan)
        {

            planTable.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(step.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)))),
                new Markup(themePalette.TextMarkup(Markup.Escape(step.Description))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(step.Status))));

        }

        AnsiConsole.Write(planTable);

    }

}

/// <summary>
/// Apprentice orchestration (requires arcanum serve).
/// </summary>
public sealed class ApprenticeCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    WatchCommands watchCommands,
    IConfirmationPrompt confirmationPrompt,
    ICliResourceCatalog? resourceCatalog = null)
{

    /// <summary>
    /// List Apprentices (GET /api/apprentices).
    /// </summary>
    /// <param name="campaignId">--campaignId, Filter by campaign GUID.</param>
    /// <param name="status">Filter by status.</param>
    /// <param name="limit">Maximum number of Apprentices to return.</param>
    public async Task<int> List(
        string? campaignId = null,
        string? status = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaignId))
        {

            if (!CliArgReader.TryParseGuid(campaignId, out Guid parsed))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        Result<ListPageResult<ApprenticeSummaryDto>> result = await apiClient
            .GetApprenticesAsync(parsedCampaignId, status, limit, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        ApprenticeSummaryDto[] apprentices = result.Value.Items;

        Table table = new();

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("ID")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Goal")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Status")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Campaign")));

        table.AddColumn(themePalette.HeadingTableColumn(Markup.Escape("Updated")));

        foreach (ApprenticeSummaryDto apprentice in apprentices)
        {

            string idShort = apprentice.Id.ToString("N")[..8].ToUpperInvariant();

            table.AddRow(
                new Markup(themePalette.TextMarkup(Markup.Escape(idShort))),
                new Markup(themePalette.TextMarkup(Markup.Escape(apprentice.Goal))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(apprentice.Status))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(apprentice.CampaignId?.ToString("D") ?? "-"))),
                new Markup(themePalette.MutedMarkup(Markup.Escape(apprentice.UpdatedAt.ToString("u")))));

        }

        AnsiConsole.Write(table);

        if (apprentices.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("No Apprentices found.")));
        }

        return 0;

    }

    /// <summary>
    /// Show Apprentice detail (GET /api/apprentices/{id}).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public async Task<int> Get(string? id, CancellationToken cancellationToken)
    {
        Guid apprenticeId;
        if (!CliArgReader.TryParseGuid(id, out apprenticeId))
        {
            if (resourceCatalog is null)
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));
                return 1;
            }

            ResourceSelectionResult<ApprenticeSummaryDto> selection = await resourceCatalog
                .SelectApprenticeAsync(id, cancellationToken)
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

            apprenticeId = selection.Value!.Id;
        }

        Result<ApprenticeDetailDto> result = await apiClient.GetApprenticeAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        ApprenticeCommandSupport.WriteApprenticeDetailPanel(result.Value, themePalette);

        return 0;

    }

    /// <summary>
    /// Create an Apprentice (POST /api/apprentices).
    /// </summary>
    /// <param name="goal">Apprentice goal: inline text, or @filename to read from a file.</param>
    /// <param name="name">Display name; defaults to a truncated form of the goal.</param>
    /// <param name="campaignId">--campaignId, Campaign GUID to associate with.</param>
    /// <param name="workspace">Workspace root to scope the Apprentice.</param>
    public async Task<int> Create(
        string? goal = null,
        string? name = null,
        string? campaignId = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrEmpty(goal))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(goal, out string resolvedGoal, out string? goalError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(goalError!)));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(resolvedGoal))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal must not be empty.")));

            return 1;
        }

        Guid? parsedCampaignId = null;

        if (!string.IsNullOrWhiteSpace(campaignId))
        {

            if (!CliArgReader.TryParseGuid(campaignId, out Guid parsed))
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        string resolvedName = string.IsNullOrWhiteSpace(name)
            ? ApprenticeCommandSupport.DeriveNameFromGoal(resolvedGoal)
            : name.Trim();

        CreateApprenticeRequest request = new(resolvedName, resolvedGoal.Trim(), parsedCampaignId, workspace);

        Result<ApprenticeDetailDto> result = await apiClient.CreateApprenticeAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Apprentice created:"),
                Markup.Escape($"{result.Value.Name} ({result.Value.Id:D})")));

        return 0;

    }

    private async Task<(bool Resolved, bool Cancelled, Guid Id)> ResolveApprenticeIdAsync(
        string? identifier,
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

        ResourceSelectionResult<ApprenticeSummaryDto> selection = await resourceCatalog
            .SelectApprenticeAsync(identifier, cancellationToken)
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

    /// <summary>
    /// Delete a terminal Apprentice (DELETE /api/apprentices/{id}).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public async Task<int> Delete(string? id, CancellationToken cancellationToken)
    {

        (bool resolved, bool cancelled, Guid apprenticeId) = await ResolveApprenticeIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (!await confirmationPrompt
                .PromptForConfirmationAsync($"Delete Apprentice {apprenticeId:D}?", cancellationToken)
                .ConfigureAwait(false))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.MutedMarkup(Markup.Escape("Apprentice deletion cancelled.")));

            return 0;
        }

        Result result = await apiClient.DeleteApprenticeAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Apprentice removed.")));

        return 0;

    }

    /// <summary>
    /// Start plan generation and execution (POST /api/apprentices/{id}/start).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public Task<int> Start(string? id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "started", apiClient.StartApprenticeAsync, cancellationToken);

    /// <summary>
    /// Pause at the next step boundary (POST /api/apprentices/{id}/pause).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public Task<int> Pause(string? id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "paused", apiClient.PauseApprenticeAsync, cancellationToken);

    /// <summary>
    /// Resume from checkpoint (POST /api/apprentices/{id}/resume).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public Task<int> Resume(string? id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "resumed", apiClient.ResumeApprenticeAsync, cancellationToken);

    /// <summary>
    /// Cancel execution (POST /api/apprentices/{id}/cancel).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public Task<int> Cancel(string? id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "cancelled", apiClient.CancelApprenticeAsync, cancellationToken);

    private async Task<int> RunLifecycleActionAsync(
        string? id,
        string actionLabel,
        Func<Guid, CancellationToken, Task<Result<string>>> invoke,
        CancellationToken cancellationToken)
    {

        (bool resolved, bool cancelled, Guid apprenticeId) = await ResolveApprenticeIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        Result<string> result = await invoke(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightLabelMarkup(Markup.Escape($"Apprentice {actionLabel}:"), Markup.Escape(apprenticeId.ToString("D"))));

        return 0;

    }

    /// <summary>
    /// Replace the remaining plan steps (POST /api/apprentices/{id}/reweave).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    /// <param name="plan">JSON array of plan steps: inline text, or @filename to read from a file.</param>
    public async Task<int> Reweave(string? id, string? plan = null, CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid apprenticeId) = await ResolveApprenticeIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (string.IsNullOrEmpty(plan))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--plan is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(plan, out string planJson, out string? planError))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape(planError!)));

            return 1;
        }

        List<PlanStep>? steps;

        try
        {
            steps = JsonSerializer.Deserialize(planJson, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.ListPlanStep);
        }
        catch (JsonException ex)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid plan JSON: {ex.Message}")));

            return 1;
        }

        if (steps is not { Count: > 0 })
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--plan must contain at least one step.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient.ReweaveApprenticeAsync(apprenticeId, steps, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Apprentice plan reweaved:"),
                Markup.Escape($"{result.Value.Plan.Count} step(s).")));

        return 0;

    }

    /// <summary>
    /// Provide Divine Intervention guidance to an escalated Apprentice (POST /api/apprentices/{id}/intervene).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    /// <param name="guidance">Guidance text for the escalated Apprentice.</param>
    public async Task<int> Intervene(string? id, string? guidance = null, CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid apprenticeId) = await ResolveApprenticeIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (string.IsNullOrWhiteSpace(guidance))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--guidance is required.")));

            return 1;
        }

        Result<string> result = await apiClient.IntervereApprenticeAsync(apprenticeId, guidance.Trim(), cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Divine Intervention submitted; Apprentice resuming.")));

        return 0;

    }

    /// <summary>
    /// Delegate a child Apprentice via The Conclave (POST /api/apprentices/{id}/cast).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    /// <param name="goal">Child Apprentice goal text.</param>
    /// <param name="name">Display name for the child Apprentice.</param>
    public async Task<int> Cast(
        string? id,
        string? goal = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {

        (bool resolved, bool cancelled, Guid apprenticeId) = await ResolveApprenticeIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (!resolved) return cancelled ? 0 : 1;

        if (string.IsNullOrWhiteSpace(goal))
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal is required.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient
            .CastApprenticeAsync(apprenticeId, goal.Trim(), name, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Apprentice.ConclaveDisabled", StringComparison.Ordinal))
            {
                CliErrorOutput.WriteMarkupLine(
                    themePalette.ErrorMarkup(
                        Markup.Escape("The Conclave is disabled; cross-Apprentice delegation is not available. Enable Arcanum:Features:Conclave on the host to use 'apprentice cast'.")));
            }
            else
            {
                CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
            }

            return 1;

        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Child Apprentice cast:"),
                Markup.Escape($"{result.Value.Name} ({result.Value.Id:D})")));

        return 0;

    }

    /// <summary>
    /// Stream live Apprentice events (GET /api/apprentices/{id}/chronicle, SSE).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    public async Task<int> Chronicle(
        string? id,
        CancellationToken cancellationToken) =>
        await watchCommands.Apprentice(
            id,
            new WatchCommandOptions(false, [], []),
            cancellationToken).ConfigureAwait(false);

}
