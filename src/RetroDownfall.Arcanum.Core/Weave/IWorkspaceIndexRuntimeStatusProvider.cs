namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>Read-only access to volatile workspace watcher and reconciliation status.</summary>
public interface IWorkspaceIndexRuntimeStatusProvider
{

    WorkspaceIndexRuntimeStatus GetRuntimeStatus(string workspacePath);

}
