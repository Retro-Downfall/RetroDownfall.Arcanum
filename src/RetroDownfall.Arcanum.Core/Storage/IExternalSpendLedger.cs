namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// Today's spend on work this instance delegated to a remote agent, kept distinct from local spend.
/// </summary>
/// <param name="KnownCostUsd">What peers actually reported. Never includes a guess for the rest.</param>
/// <param name="KnownTokens">Peer-reported tokens behind <paramref name="KnownCostUsd"/>.</param>
/// <param name="PricedSendings">Settled Sendings whose peer reported usage.</param>
/// <param name="UnpricedSendings">
/// Settled Sendings whose peer reported nothing. Counted, never costed: recording them as zero would
/// make a total look complete when part of the day's work has no price at all (issue #60/#69).
/// </param>
public sealed record ExternalSpendSummary(
    decimal KnownCostUsd,
    long KnownTokens,
    int PricedSendings,
    int UnpricedSendings)
{

    /// <summary>No delegated work settled today.</summary>
    public static readonly ExternalSpendSummary None = new(0m, 0, 0, 0);

    /// <summary>
    /// Whether any of today's delegated work has no reported price. This is itself the operator signal:
    /// a ceiling is only as meaningful as the spend it can see.
    /// </summary>
    public bool HasUnpricedDelegation => UnpricedSendings > 0;

}

/// <summary>
/// Reads what delegated (A2A) work cost today, from the durable Sending records rather than from any
/// in-memory total, so the figure survives the process that spent it.
/// </summary>
public interface IExternalSpendLedger
{

    /// <summary>Today's delegated spend, by UTC day, matching the daily budget window.</summary>
    Task<ExternalSpendSummary> GetTodayAsync(CancellationToken cancellationToken = default);

}

/// <summary>
/// Carries the current turn's budget reservation to work that is delegated outside the turn, so an
/// outbound Sending's durable row can name the reservation that paid for the turn it came from.
/// </summary>
/// <remarks>
/// Set by the Api's turn accounting and read at the MCP client send boundary, which runs inside the
/// turn's async flow. The in-process MCP server runs on its own task, so the value is bound to the
/// outgoing request rather than read from this ambient inside the tool handler
/// (see <c>ApprenticeToolInvocationBinding</c>).
/// </remarks>
public static class DelegatedSpendAttribution
{

    private static readonly AsyncLocal<Guid?> Current = new();

    /// <summary>The reservation covering the turn currently executing, when there is one.</summary>
    public static Guid? BudgetReservationId
    {
        get => Current.Value;
        set => Current.Value = value;
    }

}
