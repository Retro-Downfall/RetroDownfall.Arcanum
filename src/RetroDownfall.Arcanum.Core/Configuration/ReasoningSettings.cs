namespace RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Code-owned default for the reasoning preference surface (effort level + display toggle). Not
/// bindable — it is projected through <see cref="ArcanumRuntimeDefaults.Reasoning"/> and
/// mapped by <see cref="ArcanumRuntimeSettings"/>.
/// </summary>
public sealed record ReasoningSettings
{

    /// <summary>
    /// Default reasoning effort applied to requests that omit explicit reasoning options. Must be
    /// a valid <see cref="ReasoningEffortLevel"/> value; does not inject reasoning when null.
    /// </summary>
    public ReasoningEffortLevel? DefaultEffort { get; set; }

    /// <summary>
    /// Whether reasoning capabilities are enabled by default. Independent of the per-turn budget.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether reasoning output summaries are shown to clients by default.
    /// </summary>
    public bool Summaries { get; set; }

}
