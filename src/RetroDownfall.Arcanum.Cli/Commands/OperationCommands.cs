using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class OperationCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette)
{
    public async Task<int> List(string? kind, string? state, CancellationToken cancellationToken)
    {
        Result<LongRunningOperationDto[]> result = await apiClient
            .GetOperationsAsync(kind, state, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        Table table = new();
        table.AddColumn(themePalette.HeadingTableColumn("ID"));
        table.AddColumn(themePalette.HeadingTableColumn("Kind"));
        table.AddColumn(themePalette.HeadingTableColumn("State"));
        table.AddColumn(themePalette.HeadingTableColumn("Attempts"));
        table.AddColumn(themePalette.HeadingTableColumn("Summary"));
        foreach (LongRunningOperationDto operation in result.Value)
        {
            table.AddRow(
                themePalette.TextMarkup(Markup.Escape(operation.Id.ToString("D"))),
                themePalette.TextMarkup(Markup.Escape(operation.Kind)),
                themePalette.HighlightMarkup(Markup.Escape(operation.State.ToString())),
                themePalette.MutedMarkup(operation.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                themePalette.TextMarkup(Markup.Escape(operation.PublicSummary)));
        }

        AnsiConsole.Write(table);
        if (result.Value.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup("No durable operations found."));
        }

        return 0;
    }

    public async Task<int> Show(Guid id, CancellationToken cancellationToken)
    {
        Result<LongRunningOperationDto> result = await apiClient
            .GetOperationAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        LongRunningOperationDto operation = result.Value;
        Table table = new();
        table.Border(TableBorder.None);
        table.HideHeaders();
        table.AddColumn(string.Empty);
        table.AddColumn(string.Empty);
        AddDetail(table, "ID", operation.Id.ToString("D"));
        AddDetail(table, "Kind", operation.Kind);
        AddDetail(table, "State", operation.State.ToString());
        AddDetail(table, "Recovery", operation.RecoveryPolicy.ToString());
        AddDetail(table, "Created", operation.CreatedAt.ToString("u"));
        AddDetail(table, "Lease owner", operation.LeaseOwner ?? "(none)");
        AddDetail(table, "Lease expiry", operation.LeaseExpiresAt?.ToString("u") ?? "(none)");
        AddDetail(table, "Attempt", operation.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddDetail(table, "Checkpoint", operation.HasCheckpoint
            ? $"version {operation.CheckpointVersion}"
            : "none");
        AddDetail(table, "Summary", operation.PublicSummary);
        AddDetail(table, "Error", operation.TerminalErrorCode ?? "(none)");
        AnsiConsole.Write(new Panel(table)
        {
            Header = new PanelHeader(themePalette.HeadingBoldMarkup("Durable operation")),
            Border = BoxBorder.Rounded,
            BorderStyle = themePalette.HighlightStyle(),
        });
        return 0;
    }

    public Task<int> Cancel(Guid id, CancellationToken cancellationToken) =>
        Mutate(id, "Cancellation requested.", apiClient.CancelOperationAsync, cancellationToken);

    public Task<int> Retry(Guid id, CancellationToken cancellationToken) =>
        Mutate(id, "Operation reset to Pending.", apiClient.RetryOperationAsync, cancellationToken);

    public async Task<int> Reconcile(CancellationToken cancellationToken)
    {
        Result<LongRunningOperationReconciliationSummary> result = await apiClient
            .ReconcileOperationsAsync(cancellationToken)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        LongRunningOperationReconciliationSummary summary = result.Value;
        AnsiConsole.MarkupLine(themePalette.TextMarkup(Markup.Escape(
            $"Examined {summary.Examined}; claimed {summary.Claimed}; completed {summary.Completed}; "
            + $"failed {summary.Failed}; abandoned {summary.Abandoned}; "
            + $"attention {summary.RequiresAttention}; skipped {summary.Skipped}.")));
        return summary.RequiresAttention > 0 ? 2 : 0;
    }

    private async Task<int> Mutate(
        Guid id,
        string successMessage,
        Func<Guid, CancellationToken, Task<Result<LongRunningOperationDto>>> action,
        CancellationToken cancellationToken)
    {
        Result<LongRunningOperationDto> result = await action(id, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            AnsiConsole.MarkupLine(themePalette.ErrorMarkup(result.Error));
            return 1;
        }

        AnsiConsole.MarkupLine(themePalette.TextMarkup(Markup.Escape(successMessage)));
        return 0;
    }

    private void AddDetail(Table table, string name, string value) =>
        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape(name + ":")),
            themePalette.TextMarkup(Markup.Escape(value)));
}
