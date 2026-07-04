using System.ComponentModel;
using System.Text.Json;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using Spectre.Console;
using Spectre.Console.Cli;

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

public sealed class ApprenticeListCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeListCommand.Settings>
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

        Result<ListPageResult<ApprenticeSummaryDto>> result = await apiClient
            .GetApprenticesAsync(campaignId, settings.Status, settings.Limit, cancellationToken: cancellationToken)
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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--campaignId <ID>")]
        public string? CampaignId { get; init; }

        [CommandOption("--status <STATUS>")]
        public string? Status { get; init; }

        [CommandOption("--limit <N>")]
        public int? Limit { get; init; }

    }

}

public sealed class ApprenticeGetCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeGetCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient.GetApprenticeAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        ApprenticeCommandSupport.WriteApprenticeDetailPanel(result.Value, themePalette);

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class ApprenticeCreateCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeCreateCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (string.IsNullOrEmpty(settings.Goal))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(settings.Goal, out string goal, out string? goalError))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape(goalError!)));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal must not be empty.")));

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

        string name = string.IsNullOrWhiteSpace(settings.Name)
            ? ApprenticeCommandSupport.DeriveNameFromGoal(goal)
            : settings.Name.Trim();

        CreateApprenticeRequest request = new(name, goal.Trim(), campaignId, settings.Workspace);

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

    public sealed class Settings : CommandSettings
    {

        [CommandOption("--goal <TEXT_OR_FILE>")]
        [Description("Apprentice goal: inline text, or @filename to read from a file.")]
        public string? Goal { get; init; }

        [CommandOption("--name <NAME>")]
        [Description("Display name; defaults to a truncated form of the goal.")]
        public string? Name { get; init; }

        [CommandOption("--campaignId <ID>")]
        public string? CampaignId { get; init; }

        [CommandOption("--workspace <PATH>")]
        public string? Workspace { get; init; }

    }

}

public sealed class ApprenticeDeleteCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeDeleteCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result result = await apiClient.DeleteApprenticeAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Apprentice removed.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public abstract class ApprenticeLifecycleCommandBase(IThemePalette themePalette)
    : AsyncCommand<ApprenticeLifecycleCommandBase.Settings>
{

    protected abstract string ActionLabel { get; }

    protected abstract Task<Result<string>> InvokeAsync(Guid id, CancellationToken cancellationToken);

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        Result<string> result = await InvokeAsync(id, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.HighlightLabelMarkup(Markup.Escape($"Apprentice {ActionLabel}:"), Markup.Escape(id.ToString("D"))));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}

public sealed class ApprenticeStartCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : ApprenticeLifecycleCommandBase(themePalette)
{

    protected override string ActionLabel => "started";

    protected override Task<Result<string>> InvokeAsync(Guid id, CancellationToken cancellationToken) =>
        apiClient.StartApprenticeAsync(id, cancellationToken);

}

public sealed class ApprenticePauseCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : ApprenticeLifecycleCommandBase(themePalette)
{

    protected override string ActionLabel => "paused";

    protected override Task<Result<string>> InvokeAsync(Guid id, CancellationToken cancellationToken) =>
        apiClient.PauseApprenticeAsync(id, cancellationToken);

}

public sealed class ApprenticeResumeCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : ApprenticeLifecycleCommandBase(themePalette)
{

    protected override string ActionLabel => "resumed";

    protected override Task<Result<string>> InvokeAsync(Guid id, CancellationToken cancellationToken) =>
        apiClient.ResumeApprenticeAsync(id, cancellationToken);

}

public sealed class ApprenticeCancelCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : ApprenticeLifecycleCommandBase(themePalette)
{

    protected override string ActionLabel => "cancelled";

    protected override Task<Result<string>> InvokeAsync(Guid id, CancellationToken cancellationToken) =>
        apiClient.CancelApprenticeAsync(id, cancellationToken);

}

public sealed class ApprenticeReweaveCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeReweaveCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrEmpty(settings.Plan))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--plan is required.")));

            return 1;
        }

        if (!CliArgReader.TryReadInlineOrFile(settings.Plan, out string planJson, out string? planError))
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

        Result<ApprenticeDetailDto> result = await apiClient.ReweaveApprenticeAsync(id, steps, cancellationToken).ConfigureAwait(false);

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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--plan <JSON_OR_FILE>")]
        [Description("JSON array of plan steps: inline text, or @filename to read from a file.")]
        public string? Plan { get; init; }

    }

}

public sealed class ApprenticeInterveneCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeInterveneCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Guidance))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--guidance is required.")));

            return 1;
        }

        Result<string> result = await apiClient.IntervereApprenticeAsync(id, settings.Guidance.Trim(), cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));

            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.MutedMarkup(Markup.Escape("Divine Intervention submitted; Apprentice resuming.")));

        return 0;

    }

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--guidance <TEXT>")]
        public string? Guidance { get; init; }

    }

}

public sealed class ApprenticeCastCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeCastCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("<ID> must be a valid GUID.")));

            return 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Goal))
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(Markup.Escape("--goal is required.")));

            return 1;
        }

        Result<ApprenticeDetailDto> result = await apiClient
            .CastApprenticeAsync(id, settings.Goal.Trim(), settings.Name, cancellationToken)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {

            if (string.Equals(result.Error.Code, "Apprentice.ConclaveDisabled", StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(
                    themePalette.ErrorMarkup(
                        Markup.Escape("The Conclave is disabled; cross-Apprentice delegation is not available. Enable Arcanum:Conclave:Enabled on the host to use 'apprentice cast'.")));
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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

        [CommandOption("--goal <TEXT>")]
        public string? Goal { get; init; }

        [CommandOption("--name <NAME>")]
        public string? Name { get; init; }

    }

}

public sealed class ApprenticeChronicleCommand(ArcanumApiClient apiClient, IThemePalette themePalette)
    : AsyncCommand<ApprenticeChronicleCommand.Settings>
{

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {

        if (!CliArgReader.TryParseGuid(settings.Id, out Guid id))
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

            await foreach (ChronicleFrame frame in apiClient.StreamApprenticeChronicleAsync(id, linked.Token).ConfigureAwait(false))
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

    public sealed class Settings : CommandSettings
    {

        [CommandArgument(0, "<ID>")]
        public required string Id { get; init; }

    }

}
