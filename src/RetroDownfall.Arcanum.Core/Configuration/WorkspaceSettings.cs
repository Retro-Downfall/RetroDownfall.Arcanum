namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record WorkspaceSettings
{

    public long MaxFileReadSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum directory depth for recursive workspace file listing (<c>GET /api/workspaces/{id}/files?recursive=true</c>).
    /// Default 64; clamp 1&#8211;256.
    /// </summary>
    public int ListDirectoryMaxDepth { get; init; } = 64;

    /// <summary>
    /// Master toggle for the workspace file write/modify/delete surface
    /// (<c>PUT</c>/<c>PATCH</c>/<c>DELETE .../files</c>, <c>POST .../files/directory</c>).
    /// When <c>false</c> (default), every write/modify/delete endpoint returns <c>403 Workspace.FileWriteDisabled</c>
    /// without performing any I/O.
    /// </summary>
    public bool EnableFileWrite { get; init; } = false;

    /// <summary>
    /// Maximum byte size of file content accepted by <c>PUT /api/workspaces/{id}/files/contents</c>
    /// (and the <c>newString</c> on <c>PATCH .../files/contents</c>). Default 1 MiB; clamp 1 KiB&#8211;10 MiB.
    /// </summary>
    public long MaxFileWriteSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Maximum combined byte size of <c>oldString</c> + <c>newString</c> on
    /// <c>PATCH /api/workspaces/{id}/files/contents</c>. Default 512 KiB; clamp 1 KiB&#8211;4 MiB.
    /// </summary>
    public long MaxReplaceTextBlockBytes { get; init; } = 512 * 1024;

}
