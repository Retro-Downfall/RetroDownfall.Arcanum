namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Configuration for <strong>The Proving Grounds</strong> — the in-memory Trial runner and Inquisitor
/// adjudication subsystem. Bound from <c>Arcanum:ProvingGrounds</c>.
/// </summary>
public sealed record ProvingGroundsSettings
{

    /// <summary>
    /// Maximum number of Inquisitors allowed on a single Trial. Default <c>20</c>; clamped 1–200 at runtime.
    /// </summary>
    public int MaxInquisitorsPerTrial { get; init; } = 20;

    /// <summary>
    /// Maximum completion tokens for a Semantic Inquisitor FastModel judge call. Default <c>8</c>.
    /// </summary>
    public int SemanticJudgeMaxTokens { get; init; } = 8;

    /// <summary>
    /// Timeout in seconds for a Semantic Inquisitor judge inference call. Default <c>60</c>.
    /// </summary>
    public int SemanticJudgeTimeoutSeconds { get; init; } = 60;

}
