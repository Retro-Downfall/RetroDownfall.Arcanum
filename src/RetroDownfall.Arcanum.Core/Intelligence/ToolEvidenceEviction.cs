namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Cumulative complete-exchange removal operations in a logical turn. Precommit provider fallback
/// can remove the same reconstructed historical exchange again. Correlation uses only
/// one-based completed tool-round ordinals from this turn, retaining at most the latest eight.
/// Historical exchanges have no current-turn ordinal. No tool names, arguments, results or IDs escape.
/// </summary>
public sealed record ToolEvidenceEviction(
    int Exchanges,
    int Messages,
    int Calls,
    IReadOnlyList<int> RecentRoundOrdinals,
    int OmittedRoundOrdinals,
    int HistoricalExchanges);
