namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Runtime projection for Apprentices. Activation comes from
/// <c>Arcanum:Features:Apprentices</c>, host concurrency/backpressure from
/// <c>Arcanum:Execution</c>, and workflow/channel mechanics are code-owned.
/// </summary>
public sealed record ApprenticeSettings
{

    public bool Enabled { get; set; } = true;

    public int MaxConcurrentApprentices { get; set; } = 5;

    /// <summary>
    /// Per-subscriber bounded channel capacity for Chronicle and session event hubs. Applied
    /// when a per-apprentice / per-session hub is first created; existing hubs retain their
    /// original code-owned capacity.
    /// </summary>
    public int ChronicleChannelCapacity { get; set; } = 1000;

    public bool EnableShiftingFate { get; set; } = true;

    public bool EnableDivineIntervention { get; set; } = true;

}
