namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record UnseenServantJob
{

    public string Name { get; init; } = string.Empty;

    public int IntervalMinutes { get; init; } = 60;

    public string TargetSpell { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

}
