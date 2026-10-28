using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Cli.Infrastructure;

using RetroDownfall.Arcanum.Core.Desktop;

using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using Spectre.Console;

namespace RetroDownfall.Arcanum.Cli.Commands;

public sealed class OperationCommands(
    ArcanumApiClient apiClient,
    IThemePalette themePalette,
    IConsoleDispatcher dispatcher)
{
    public async Task<int> List(
        string? kind,
        string? state,
        int? limit,
        int offset,
        CancellationToken cancellationToken)
    {
        int pageSize = limit ?? LongRunningOperationQuery.DefaultLimit;

        if (!new LongRunningOperationQuery(Limit: pageSize, Offset: offset).HasValidPageBounds)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(new Error(
                ErrorCodes.Validation.InvalidQuery,
                "The operation page requires --limit between 1 and 500 and a non-negative --offset whose sum with the page size fits an integer.")));

            return (int)CliExitCode.ConfigurationError;
        }

        int? continuation = null;

        Result<HostListing<LongRunningOperationDto>> result = await HostPageWalker
            .ReadAsync<LongRunningOperationDto, int>(
                "operation list",
                singlePage: limit is not null,
                async (cursor, token) =>
                {
                    int pageOffset = cursor ?? offset;

                    Result<ListPageResult<LongRunningOperationDto>> response = await apiClient
                        .GetOperationsAsync(kind, state, pageSize, pageOffset, token)
                        .ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();

                    Result<HostPage<LongRunningOperationDto, int>> page = ValidatePage(
                        response,
                        pageSize,
                        pageOffset);

                    if (page.IsSuccess) continuation = page.Value.Next;

                    return page;
                },
                cancellationToken,
                firstPageCursor: offset)
            .ConfigureAwait(false);

        if (result.IsFailure)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.ErrorMarkup(result.Error));

            return CliFailureExit.ExitCode(result.Error);
        }

        cancellationToken.ThrowIfCancellationRequested();

        LongRunningOperationDto[] rows = result.Value.Items;

        if (CliInvocationContext.Current.Json)
        {
            dispatcher.WriteJson(rows, ArcanumJsonContext.Default.LongRunningOperationDtoArray);

            WriteContinuation(continuation, pageSize, kind, state);

            return 0;
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

        ListingTableResult rendered = ListingTable.Build(themePalette, columns, rows, width);

        AnsiConsole.Write(rendered.Table);

        if (ListingTable.HiddenColumnsNotice(rendered, width) is { } hiddenNotice)
        {
            CliErrorOutput.WriteMarkupLine(themePalette.MutedMarkup(Markup.Escape(hiddenNotice)));
        }

        if (rows.Length == 0)
        {
            AnsiConsole.MarkupLine(themePalette.MutedMarkup("No durable operations found."));
        }

        WriteContinuation(continuation, pageSize, kind, state);

        return 0;
    }

    private static Result<HostPage<LongRunningOperationDto, int>> ValidatePage(
        Result<ListPageResult<LongRunningOperationDto>> response,
        int limit,
        int offset)
    {
        if (response.IsFailure)
        {
            return Result<HostPage<LongRunningOperationDto, int>>.Failure(response.Error);
        }

        ListPageResult<LongRunningOperationDto> page = response.Value;

        if (page is null)
        {
            return Result<HostPage<LongRunningOperationDto, int>>.Failure(
                HostPageWalker.NoProgressError(
                    "operation list",
                    "returned an invalid operation page or continuation",
                    0));
        }

        bool validContinuation = page.HasMore
            ? page.Items.Length > 0
                && page.NextOffset is { } next
                && next > offset
                && (long)next == (long)offset + page.Items.Length
                && next <= int.MaxValue - limit
            : page.NextOffset is null;

        if (page.Items.Length > limit || page.NextBeforeUpdatedAt is not null || !validContinuation)
        {
            return Result<HostPage<LongRunningOperationDto, int>>.Failure(
                HostPageWalker.NoProgressError(
                    "operation list",
                    "returned an invalid operation page or continuation",
                    page.Items.Length));
        }

        return Result<HostPage<LongRunningOperationDto, int>>.Success(
            new HostPage<LongRunningOperationDto, int>(page.Items, page.HasMore, page.NextOffset));
    }

    private void WriteContinuation(int? offset, int limit, string? kind, string? state)
    {
        if (offset is not { } next) return;

        string command = FormattableString.Invariant($"arcanum operation list --limit {limit} --offset {next}");

        if (!string.IsNullOrEmpty(kind)) command += " --kind " + CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(kind);

        if (!string.IsNullOrEmpty(state)) command += " --state " + CommandDisplayFormatter.QuoteArgumentForCurrentPlatform(state);

        dispatcher.WriteDiagnostic($"More operations remain. Continue: {command}");
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
