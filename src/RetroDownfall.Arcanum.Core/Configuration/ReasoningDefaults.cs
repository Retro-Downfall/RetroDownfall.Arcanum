using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ReasoningDefaults
{
    public ReasoningEffortLevel? DefaultEffort { get; set; } = ReasoningEffortLevel.Medium;

    public bool Enabled { get; set; } = true;
    public bool Summaries { get; set; }
}
