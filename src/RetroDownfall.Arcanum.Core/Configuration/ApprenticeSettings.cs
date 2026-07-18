namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ApprenticeSettings
{

    public bool Enabled { get; set; } = true;

    public int MaxConcurrentApprentices { get; set; } = 5;

    public int StepTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Per-subscriber bounded channel capacity for Chronicle and session event hubs. Applied
    /// when a per-apprentice / per-session hub is first created; existing hubs retain their
    /// original capacity, so a config reload affects only new hubs (effectively startup-only
    /// for an existing hub). Clamp 100–10,000.
    /// </summary>
    public int ChronicleChannelCapacity { get; set; } = 1000;

    public int MaxStepRetries { get; set; } = 2;

    public int RetryBackoffSeconds { get; set; } = 5;

    public int RetryBackoffMaxSeconds { get; set; } = 60;

    public bool EnableShiftingFate { get; set; } = true;

    public bool EnableDivineIntervention { get; set; } = true;

    public int MaxSimulacra { get; set; } = 3;

    public int MaxRunSteps { get; set; } = 100;

    public int MaxRunDurationMinutes { get; set; } = 480;

    public int MaxReweavesPerRun { get; set; } = 10;

    public int MaxPendingStarts { get; set; } = 100;

}
