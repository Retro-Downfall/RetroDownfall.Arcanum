namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// OpenAI-compatible <c>/v1/files</c> upload storage. Bound from <c>Arcanum:Files</c>. Distinct from
/// <see cref="WorkspaceSettings.MaxFileReadSizeBytes"/>/<c>MaxFileWriteSizeBytes</c> — those bound
/// filesystem access inside a registered workspace; this bounds standalone file uploads stored under
/// <see cref="Storage.ArcanumPaths.FilesDirectory"/> for later use by <c>/v1/batches</c> and similar.
/// </summary>
public sealed record FilesSettings
{

    /// <summary>Maximum upload size in bytes. Default 512 MiB; clamped 1 MiB – 10 GiB at runtime.</summary>
    public long MaxUploadSizeBytes { get; init; } = 512L * 1024L * 1024L;

    /// <summary>
    /// Allowed MIME types for uploads. Empty (default) means no operator-configured restriction —
    /// the built-in extension/declared-MIME-type cross-check (<c>UploadedFileMimeValidator</c>) still
    /// applies independently as a baseline defense.
    /// </summary>
    public string[] AllowedMimeTypes { get; init; } = [];

}
