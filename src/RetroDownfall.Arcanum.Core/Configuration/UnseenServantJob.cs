namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record UnseenServantJob
{

    public string Name { get; set; } = string.Empty;

    public int IntervalMinutes { get; set; } = 60;

    public string TargetSpell { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

}
