namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Detached runtime defaults for OpenAI-compatible <c>/v1/files</c> upload storage. Upload size is
/// code-owned; the retained MIME policy is projected from
/// <c>Arcanum:Security:AllowedUploadMimeTypes</c>.
/// </summary>
public sealed record FilesSettings
{

    /// <summary>Internal maximum upload size in bytes.</summary>
    public long MaxUploadSizeBytes { get; set; } = 512L * 1024L * 1024L;

    /// <summary>
    /// Effective MIME types for uploads. Empty means no operator-configured restriction —
    /// the built-in extension/declared-MIME-type cross-check (<c>UploadedFileMimeValidator</c>) still
    /// applies independently as a baseline defense.
    /// </summary>
    public string[] AllowedMimeTypes { get; set; } = [];

}
