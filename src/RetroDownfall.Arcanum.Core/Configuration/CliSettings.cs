namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CliSettings
{

    public long MaxAttachFileSizeBytes { get; init; } = 1_048_576L;

    public int MaxAttachedFilesPerRequest { get; init; } = 32;

    public int MaxAttachedFileRelativePathChars { get; init; } = 4096;

    public ArcanumTheme Theme { get; init; } = ArcanumTheme.SystemDefault;

    public ThemeColors ThemeColors { get; init; } = new();

    public bool ShowManaBar { get; init; } = true;

    /// <summary>
    /// Timeout (seconds) for the <c>arcanum doctor</c> API health probe against
    /// <c>http://localhost:{Arcanum:Host:Port}/api/health</c>. Default 2; clamp 1&#8211;60.
    /// Increase for slow startups (cold containers, hardware-accelerated provider warmup).
    /// </summary>
    public int DoctorHealthTimeoutSeconds { get; init; } = 2;

}


