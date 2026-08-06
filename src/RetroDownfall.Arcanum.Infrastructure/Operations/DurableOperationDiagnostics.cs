using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Operations;

/// <summary>
/// Ledger-backed <see cref="IDurableOperationDiagnostics"/> (#40, requirement 11).
/// </summary>
/// <remarks>
/// Three distinct failures share one surface because an operator experiences them the same way —
/// "something durable is stuck and nothing is fixing it": a lease that expired without anyone
/// claiming it, an operation recovery has explicitly given up on, and a kind that has no owning
/// handler at all. Only the last is a build-time bug, but all three otherwise stay invisible until
/// someone goes looking.
/// </remarks>
internal sealed class DurableOperationDiagnostics(
    ILongRunningOperationStore store,
    LongRunningOperationReconciler reconciler) : IDurableOperationDiagnostics
{
    /// <summary>Bounded so a badly stuck installation cannot turn a health probe into a table scan.</summary>
    private const int InspectionLimit = 200;

    public async Task<DurableOperationDiagnosticsReport> InspectAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LongRunningOperation> stale = await store
            .FindExpiredAsync(utcNow, InspectionLimit, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<LongRunningOperation> awaitingRepair = await store
            .ListAsync(
                new LongRunningOperationQuery(
                    State: LongRunningOperationState.ReconciliationRequired,
                    Limit: InspectionLimit),
                cancellationToken)
            .ConfigureAwait(false);

        // The fake and SQL stores differ on whether ListAsync honours the state filter, and a health
        // probe must not over-report; filter defensively rather than trusting the query shape.
        LongRunningOperation[] needsRepair =
        [
            .. awaitingRepair.Where(static operation =>
                operation.State == LongRunningOperationState.ReconciliationRequired),
        ];

        DurableOperationRepairItem[] items =
        [
            .. needsRepair
                .GroupBy(static operation => (operation.Kind, operation.TerminalErrorCode))
                .Select(static group => new DurableOperationRepairItem(
                    group.Key.Kind,
                    group.Count(),
                    group.Key.TerminalErrorCode,
                    LongRunningOperationRecoveryRegistry.Find(group.Key.Kind)?.ManualRepairGuidance
                        ?? "This operation kind is not in the recovery registry; report it as a defect."))
                .OrderBy(static item => item.Kind, StringComparer.Ordinal),
        ];

        return new DurableOperationDiagnosticsReport(
            stale.Count,
            needsRepair.Length,
            reconciler.MissingHandlerKinds,
            items);
    }
}
