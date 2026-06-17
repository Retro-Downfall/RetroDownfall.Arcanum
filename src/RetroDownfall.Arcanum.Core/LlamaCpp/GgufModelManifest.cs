namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// On-disk manifest for a cached GGUF entry.
/// </summary>
public sealed record GgufModelManifest
{

    public string SourceUrl { get; init; } = string.Empty;

    public string? Etag { get; init; }

    public string? Sha256 { get; init; }

    public DateTimeOffset DownloadedAt { get; init; }

    public DateTimeOffset LastAccessedAt { get; init; }

    public long Size { get; init; }

}
