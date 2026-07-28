namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ClientToolForwardingSettings
{

    public bool Enabled { get; set; }

    public int MaxClientTools { get; set; } = 20;

}
