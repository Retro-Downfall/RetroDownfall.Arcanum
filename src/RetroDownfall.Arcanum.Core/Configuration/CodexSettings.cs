namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>Code-owned CODEX storage envelope; this is not a public configuration root.</summary>
public sealed record CodexSettings
{

    public long MaxSizeBytes { get; set; } = 262_144L;

}
