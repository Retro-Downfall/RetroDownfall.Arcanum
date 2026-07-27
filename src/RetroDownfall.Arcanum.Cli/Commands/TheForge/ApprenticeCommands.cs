using System.Text.Json;
using ConsoleAppFramework;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands.TheForge;

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
/// The Forge Apprentice orchestration (requires arcanum serve).
/// </summary>
public sealed class ApprenticeCommands(ArcanumApiClient apiClient, IThemePalette themePalette)
{

    /// <summary>
    /// List Apprentices (GET /api/apprentices).
    /// </summary>
    /// <param name="campaignId">--campaignId, Filter by campaign GUID.</param>
    /// <param name="status">Filter by status.</param>
    /// <param name="limit">Maximum number of Apprentices to return.</param>
    [Command("list")]
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
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--campaignId must be a valid GUID.")));

                return 1;
            }

            parsedCampaignId = parsed;

        }

        Result<ListPageResult<ApprenticeSummaryDto>> result = await apiClient
            .GetApprenticesAsync(parsedCampaignId, status, limit, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

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
    [Command("get")]
    public async Task<int> Get([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient.GetApprenticeAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

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
    [Command("create")]
    public async Task<int> Create(
        string? goal = null,
        string? name = null,
        string? campaignId = null,
        string? workspace = null,
        CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrEmpty(goal))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(goal, out string resolvedGoal, out string? goalError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(goalError!)));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(resolvedGoal))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal must not be empty.")));

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

        string resolvedName = string.IsNullOrWhiteSpace(name)
            ? ApprenticeCommandSupport.DeriveNameFromGoal(resolvedGoal)
            : name.Trim();

        CreateApprenticeRequest request = new(resolvedName, resolvedGoal.Trim(), parsedCampaignId, workspace);

        Result<ApprenticeDetailDto> result = await apiClient.CreateApprenticeAsync(request, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(
            themePalette.HighlightLabelMarkup(
                Markup.Escape("Apprentice created:"),
                Markup.Escape($"{result.Value.Name} ({result.Value.Id:D})")));

        return 0;

    }

    /// <summary>
    /// Delete a terminal Apprentice (DELETE /api/apprentices/{id}).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    [Command("delete")]
    public async Task<int> Delete([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeleteApprenticeAsync(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Apprentice removed.")));

        return 0;

    }

    /// <summary>
    /// Start plan generation and execution (POST /api/apprentices/{id}/start).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    [Command("start")]
    public Task<int> Start([Argument] string id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "started", apiClient.StartApprenticeAsync, cancellationToken);

    /// <summary>
    /// Pause at the next step boundary (POST /api/apprentices/{id}/pause).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    [Command("pause")]
    public Task<int> Pause([Argument] string id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "paused", apiClient.PauseApprenticeAsync, cancellationToken);

    /// <summary>
    /// Resume from checkpoint (POST /api/apprentices/{id}/resume).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    [Command("resume")]
    public Task<int> Resume([Argument] string id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "resumed", apiClient.ResumeApprenticeAsync, cancellationToken);

    /// <summary>
    /// Cancel execution (POST /api/apprentices/{id}/cancel).
    /// </summary>
    /// <param name="id">Apprentice GUID.</param>
    [Command("cancel")]
    public Task<int> Cancel([Argument] string id, CancellationToken cancellationToken) =>
        RunLifecycleActionAsync(id, "cancelled", apiClient.CancelApprenticeAsync, cancellationToken);

    private async Task<int> RunLifecycleActionAsync(
        string id,
        string actionLabel,
        Func<Guid, CancellationToken, Task<Result<string>>> invoke,
        CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<string> result = await invoke(apprenticeId, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

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
    [Command("reweave")]
    public async Task<int> Reweave([Argument] string id, string? plan = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrEmpty(plan))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--plan is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(plan, out string planJson, out string? planError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(planError!)));

            return 1;
        }

        List<PlanStep>? steps;

        try
        {
            steps = JsonSerializer.Deserialize(planJson, RetroDownfall.Arcanum.Api.Serialization.ArcanumJsonContext.Default.ListPlanStep);
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape($"Invalid plan JSON: {ex.Message}")));

            return 1;
        }

        if (steps is not { Count: > 0 })
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--plan must contain at least one step.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient.ReweaveApprenticeAsync(apprenticeId, steps, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

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
    [Command("intervene")]
    public async Task<int> Intervene([Argument] string id, string? guidance = null, CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(guidance))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--guidance is required.")));

            return 1;
        }

        Result<string> result = await apiClient.IntervereApprenticeAsync(apprenticeId, guidance.Trim(), cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

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
    [Command("cast")]
    public async Task<int> Cast(
        [Argument] string id,
        string? goal = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal is required.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient
            .CastApprenticeAsync(apprenticeId, goal.Trim(), name, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Apprentice.ConclaveDisabled", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(
                        Markup.Escape("The Conclave is disabled; cross-Apprentice delegation is not available. Enable Arcanum:Features:Conclave on the host to use 'apprentice cast'.")));
            }
            else
            {
                AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
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
    [Command("chronicle")]
    public async Task<int> Chronicle([Argument] string id, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(id, out Guid apprenticeId))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;

            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        bool sawError = false;

        try
        {

            await foreach (ChronicleFrame frame in apiClient.StreamApprenticeChronicleAsync(apprenticeId, linked.Token).ConfigureAwait(false))
            {

                if (string.Equals(frame.Type, "error", StringComparison.Ordinal))
                {
                    AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(frame.Message)));

                    sawError = true;

                    break;
                }

                if (string.Equals(frame.Type, "eventsDropped", StringComparison.Ordinal))
                {
                    AnsiConsole.MarkupLine(
                        themePalette.ErrorMarkup(Markup.Escape("\u26a0 Some Chronicle events were dropped (slow reader).")));

                    continue;
                }

                string timestampText = frame.Timestamp?.ToString("u") ?? "-";

                bool isFailure = frame.Type.Contains("Failed", StringComparison.OrdinalIgnoreCase);

                string line = isFailure
                    ? themePalette.ErrorLabelMarkup(Markup.Escape($"[{timestampText}] {frame.Type}"), Markup.Escape(frame.Message))
                    : themePalette.MutedLabelMarkup(Markup.Escape($"[{timestampText}] {frame.Type}"), Markup.Escape(frame.Message));

                AnsiConsole.MarkupLine(line);

            }

        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }

        return sawError ? 1 : 0;

    }

}
