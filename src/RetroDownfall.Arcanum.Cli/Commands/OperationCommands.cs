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
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
            return CliFailureExit.ExitCode(result.Error);
        }

        IReadOnlyList<ListingColumn<LongRunningOperationDto>> columns =
        [
            new("ID", static operation => operation.Id.ToString("D"), ListingColumnRole.Fixed),
            new("Kind", static operation => operation.Kind, ListingColumnRole.Optional, DropOrder: 1),
            new("State", static operation => operation.State.ToString(), ListingColumnRole.Fixed),
            new("Attempts", static operation => operation.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture), ListingColumnRole.Fixed, Muted: true),
            new("Summary", static operation => operation.PublicSummary, ListingColumnRole.Optional, DropOrder: 2),
        ];

        int width = AnsiConsole.Profile.Width;

        ListingTableResult rendered = ListingTable.Build(themePalette, columns, result.Value, width);

        AnsiConsole.Write(rendered.Table);

        if (ListingTable.HiddenColumnsNotice(rendered, width) is { } hiddenNotice)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.MutedMarkup(Markup.Escape(hiddenNotice)));
        }

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
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
            return CliFailureExit.ExitCode(result.Error);
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
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
            return CliFailureExit.ExitCode(result.Error);
        }

        LongRunningOperationReconciliationSummary summary = result.Value;
        AnsiConsole.MarkupLine(themePalette.TextMarkup(Markup.Escape(
            $"Examined {summary.Examined}; claimed {summary.Claimed}; completed {summary.Completed}; "
            + $"failed {summary.Failed}; abandoned {summary.Abandoned}; "
            + $"attention {summary.RequiresAttention}; skipped {summary.Skipped}.")));
        if (summary.OwnerRecoveryRequired is { } ownerRecoveryRequired)
        {
            AnsiConsole.MarkupLine(themePalette.TextMarkup($"owner recovery required {ownerRecoveryRequired}."));
        }

        if (summary.OwnerRecoveryRequired > 0)
        {
            AnsiConsole.MarkupLine(themePalette.TextMarkup(Markup.Escape(LongRunningOperationRecoveryRegistry.OwnerRecoveryGuidance)));
        }

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
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));
            return CliFailureExit.ExitCode(result.Error);
        }

        AnsiConsole.MarkupLine(themePalette.TextMarkup(Markup.Escape(successMessage)));
        return 0;
    }

    private void AddDetail(Table table, string name, string value) =>
        table.AddRow(
            themePalette.MutedMarkup(Markup.Escape(name + ":")),
            themePalette.TextMarkup(Markup.Escape(value)));
}
