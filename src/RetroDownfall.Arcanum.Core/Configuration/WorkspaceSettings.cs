namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record WorkspaceSettings
{

    /// <summary>
    /// Optional default workspace root for spell management and workspace-scoped API routes.
    /// Relative paths resolve against the process current directory.
    /// </summary>
    public string? DefaultRoot { get; set; }

    /// <summary>
    /// Master toggle for the workspace file write/modify/delete surface
    /// (<c>PUT</c>/<c>PATCH</c>/<c>DELETE .../files</c>, <c>POST .../files/directory</c>).
    /// When <c>false</c> (default), every write/modify/delete endpoint returns <c>403 Workspace.FileWriteDisabled</c>
    /// without performing any I/O.
    /// </summary>
    public bool EnableFileWrite { get; set; } = false;

}
