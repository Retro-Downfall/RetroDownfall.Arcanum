namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record WorkspaceSettings
{

    public long MaxFileReadSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum directory depth for recursive workspace file listing (<c>GET /api/workspaces/{id}/files?recursive=true</c>).
    /// Default 64; clamp 1&#8211;256.
    /// </summary>
    public int ListDirectoryMaxDepth { get; init; } = 64;

}
