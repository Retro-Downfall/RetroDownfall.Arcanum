namespace RetroDownfall.Arcanum.Core.LlamaCpp;

/// <summary>
/// Progress frame for GGUF download (NDJSON stream and <see cref="IProgress{T}"/>).
/// </summary>
public sealed record LlamaPullProgress
{

    public string CacheKey { get; init; } = string.Empty;

    public long BytesDownloaded { get; init; }

    public long? TotalBytes { get; init; }

    public double? Percent { get; init; }

    public bool Completed { get; init; }

    public string? Error { get; init; }

    public string? Warning { get; init; }

}
