namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ApprenticeSettings
{

    public bool Enabled { get; init; } = true;

    public int MaxConcurrentApprentices { get; init; } = 5;

    public int StepTimeoutMinutes { get; init; } = 30;

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
