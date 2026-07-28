namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CodexSettings
{

    public long MaxSizeBytes { get; set; } = 262_144L;

}
