namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ClientToolForwardingSettings
{

    public bool Enabled { get; init; }

    public int MaxClientTools { get; init; } = 20;

}
