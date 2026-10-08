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

    /// <summary>
    /// Operator opt-out from the protected-metadata guard. When <c>false</c> (default), model-driven
    /// tools (<c>write_file</c>, <c>replace_text_block</c>, <c>apply_patch</c>) and the workspace file
    /// write API refuse to create, modify, rename or delete anything under <c>.git</c> (at any depth) or
    /// the first-level <c>.arcanum</c> marker directory. A hook planted in <c>.git</c> runs with the
    /// operator's full identity on the next <c>git</c> command, outside any tool jail, so the default
    /// is the safe state. Setting this to <c>true</c> lifts the refusal for every one of those write
    /// paths; it does not widen workspace containment, and it does not replace the master
    /// <see cref="EnableFileWrite"/> toggle for the API routes.
    /// </summary>
    /// <remarks>
    /// A <c>{ get; set; }</c> property, like every other key in this record: the configuration binding
    /// generator silently skips <c>init</c>-only properties (dotnet/runtime#107856), which would leave the
    /// guard permanently on while <c>arcanum.json</c> said otherwise.
    /// </remarks>
    public bool AllowProtectedPathWrites { get; set; } = false;
}
