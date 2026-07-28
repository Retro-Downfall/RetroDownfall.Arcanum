namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record CliSettings
{

    public long MaxAttachFileSizeBytes { get; set; } = 1_048_576L;

    public int MaxAttachedFilesPerRequest { get; set; } = 32;

    public int MaxAttachedFileRelativePathChars { get; set; } = 4096;

    public ArcanumTheme Theme { get; set; } = ArcanumTheme.SystemDefault;

    public ThemeColors ThemeColors { get; set; } = new();

    public bool ShowManaBar { get; set; } = true;

    /// <summary>
    /// Timeout (seconds) for the <c>arcanum doctor</c> API health probe (loopback HTTP on
    /// <c>Host:Port</c>, or HTTPS on <c>Host:Https:Port</c> when ListenAny / <c>ARCANUM_HOST_ANY</c>
    /// is effective). Default 2; clamp 1&#8211;60. Increase for slow startups (cold containers,
    /// hardware-accelerated provider warmup).
    /// </summary>
    public int DoctorHealthTimeoutSeconds { get; set; } = 2;

    /// <summary>
    /// Timeout (seconds) for non-streaming CLI API calls (<c>lore</c>, <c>daemon jobs</c>,
    /// <c>llama status</c>, session queries, etc.). Default 60; clamp 1&#8211;600.
    /// Streaming verbs (<c>ask</c>, <c>chat</c>, <c>llama pull</c>) use an unbounded client.
    /// </summary>
    public int ApiRequestTimeoutSeconds { get; set; } = 60;

}


