namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ApprenticeSettings
{

    public bool Enabled { get; init; } = true;

    public int MaxConcurrentApprentices { get; init; } = 5;

    public int StepTimeoutMinutes { get; init; } = 30;

    public int ChronicleChannelCapacity { get; init; } = 1000;

}
