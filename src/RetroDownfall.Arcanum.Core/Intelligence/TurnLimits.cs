namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Independent bounded limits for model calls, tool rounds, cost, time, and bytes.</summary>
public sealed record TurnLimits(
    int MaxModelCalls,
    int MaxToolRounds,
    int MaxToolCalls,
    int MaxToolResultTokens,
    int MaxToolResultBytes,
    TimeSpan MaxElapsedTime,
    decimal MaxEstimatedCostUsd,
    decimal MaxReservedCostUsd);
