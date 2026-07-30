using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Cli.Commands;

namespace RetroDownfall.Arcanum.Cli.Infrastructure;

internal static partial class CliCommandTree
{
    private static Command BuildOperation(IServiceProvider serviceProvider)
    {
        OperationCommands handler = serviceProvider.GetRequiredService<OperationCommands>();
        Command operation = new("operation", "Inspect and repair durable long-running operations.");

        Command list = new("list", "List durable operations.");
        Option<string?> kind = new("--kind") { Description = "Filter by registered operation kind." };
        Option<string?> state = new("--state") { Description = "Filter by lifecycle state." };
        list.Add(kind);
        list.Add(state);
        list.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.List(
                result.GetValue(kind),
                result.GetValue(state),
                cancellationToken).ConfigureAwait(false));

        Argument<Guid> id = new("id") { Description = "Durable operation ID." };
        Command show = new("show", "Show safe operation detail (checkpoint payloads are never returned).");
        show.Add(id);
        show.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Show(result.GetValue(id), cancellationToken).ConfigureAwait(false));

        Argument<Guid> cancelId = new("id") { Description = "Durable operation ID." };
        Command cancel = new("cancel", "Request cancellation through a compare-and-swap transition.");
        cancel.Add(cancelId);
        cancel.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Cancel(result.GetValue(cancelId), cancellationToken).ConfigureAwait(false));

        Argument<Guid> retryId = new("id") { Description = "Durable operation ID." };
        Command retry = new("retry", "Reset a failed, abandoned, or repair-required operation to Pending.");
        retry.Add(retryId);
        retry.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Retry(result.GetValue(retryId), cancellationToken).ConfigureAwait(false));

        Command reconcile = new("reconcile", "Run bounded recovery now.");
        reconcile.SetAction(async (ParseResult result, CancellationToken cancellationToken) =>
            await handler.Reconcile(cancellationToken).ConfigureAwait(false));

        operation.Add(list);
        operation.Add(show);
        operation.Add(cancel);
        operation.Add(retry);
        operation.Add(reconcile);
        return operation;
    }
}
