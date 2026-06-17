namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CodexSettings
{

    public long MaxSizeBytes { get; init; } = 262_144L;

}
