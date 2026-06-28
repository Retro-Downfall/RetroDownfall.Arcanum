namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ApprenticeSettings
{

    public bool Enabled { get; init; } = true;

    public int MaxConcurrentApprentices { get; init; } = 5;

    public int StepTimeoutMinutes { get; init; } = 30;

    /// <summary>
    /// Per-subscriber bounded channel capacity for Chronicle and session event hubs. Applied
    /// when a per-apprentice / per-session hub is first created; existing hubs retain their
    /// original capacity, so a config reload affects only new hubs (effectively startup-only
    /// for an existing hub). Clamp 100–10,000.
    /// </summary>
    public int ChronicleChannelCapacity { get; init; } = 1000;

    public int MaxStepRetries { get; init; } = 2;

    public int RetryBackoffSeconds { get; init; } = 5;

    public int RetryBackoffMaxSeconds { get; init; } = 60;

    public bool EnableShiftingFate { get; init; } = true;

    public bool EnableDivineIntervention { get; init; } = true;

    public int MaxSimulacra { get; init; } = 3;

    public int MaxRunSteps { get; init; } = 100;

    public int MaxRunDurationMinutes { get; init; } = 480;

    public int MaxReweavesPerRun { get; init; } = 10;

    public int MaxPendingStarts { get; init; } = 100;

}
