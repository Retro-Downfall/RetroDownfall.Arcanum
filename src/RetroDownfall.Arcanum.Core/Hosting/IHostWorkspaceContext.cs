namespace RetroDownfall.Arcanum.Core.Hosting;

/// <summary>
/// Host-level default workspace path for API routes that scope to a project directory.
/// </summary>
public interface IHostWorkspaceContext
{

    /// <summary>
    /// Normalized absolute workspace path from <c>Arcanum:Workspaces:DefaultRoot</c>, or
    /// <c>null</c> when unset.
    /// </summary>
    string? WorkspacePath { get; }

}
