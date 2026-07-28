namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Opinionated internal turn ceilings. Only <see cref="MaxToolRounds"/> is wired to
/// <see cref="IntelligenceSettings.MaxToolInferenceRounds"/> today; the remaining constants document
/// the composition contract for reservation and per-call context enforcement so those
/// paths do not invent divergent defaults.
/// </summary>
public static class TurnLimitsDefaults
{

    public const int MaxToolRounds = 8;

    public const int MaxModelCalls = 12;

    public const int MaxToolCalls = 24;

    public const int MaxToolCallsPerRound = 8;

    public const int MaxSideEffectingToolCalls = 4;

}
