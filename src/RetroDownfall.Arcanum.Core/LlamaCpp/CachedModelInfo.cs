namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Summary of one cached GGUF model for API and CLI listing.
/// </summary>
public sealed record CachedModelInfo
{

    public string CacheKey { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public string? Sha256 { get; init; }

    public long Size { get; init; }

    public DateTimeOffset DownloadedAt { get; init; }

    public DateTimeOffset LastAccessedAt { get; init; }

}
