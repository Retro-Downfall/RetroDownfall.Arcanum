using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Operations;

/// <summary>
/// One durable-operation state an operator has to resolve by hand, with the registry's guidance for
/// it (#40, requirement 11).
/// </summary>
public sealed record DurableOperationRepairItem(
    string Kind,
    int Count,
    string? TerminalErrorCode,
    string Guidance);

/// <summary>
/// Operator-facing summary of everything the durable-operation ledger cannot fix by itself.
/// </summary>
/// <remarks>
/// Every field is drawn from a closed vocabulary — bounded kinds, ledger states, and error codes —
/// because this reaches <c>arcanum doctor</c>, <c>GET /api/health</c>, and Prometheus labels. It
/// deliberately carries no operation ids, public summaries, or checkpoint content.
/// </remarks>
public sealed record DurableOperationDiagnosticsReport(
    int StaleOperations,
    int OperationsAwaitingRepair,
    IReadOnlyList<string> KindsWithoutHandler,
    IReadOnlyList<DurableOperationRepairItem> RepairItems)
{
    public static DurableOperationDiagnosticsReport Empty { get; } = new(0, 0, [], []);

    public bool NeedsAttention =>
        StaleOperations > 0 || OperationsAwaitingRepair > 0 || KindsWithoutHandler.Count > 0;

    /// <summary>Bounded, public-safe one-liner for health and doctor output.</summary>
    public string Describe()
    {
        if (!NeedsAttention)
        {
            return "No stale operations, failed reconciliations, or unowned operation kinds.";
        }

        StringBuilder detail = new();

        _ = detail.Append(CultureInfo.InvariantCulture, $"stale={StaleOperations}; ");
        _ = detail.Append(CultureInfo.InvariantCulture, $"awaiting_repair={OperationsAwaitingRepair}");

        foreach (DurableOperationRepairItem item in RepairItems)
        {
            _ = detail.Append(CultureInfo.InvariantCulture,
                $"; {item.Kind}×{item.Count} ({item.TerminalErrorCode ?? "unknown"}): {item.Guidance}");
        }

        if (KindsWithoutHandler.Count > 0)
        {
            _ = detail.Append(CultureInfo.InvariantCulture,
                $"; kinds without a recovery handler: {string.Join(", ", KindsWithoutHandler)}");
        }

        return detail.ToString();
    }
}

/// <summary>
/// Reads the durable-operation ledger for states that need a human. Implemented in Infrastructure.
/// </summary>
public interface IDurableOperationDiagnostics
{
    Task<DurableOperationDiagnosticsReport> InspectAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}
