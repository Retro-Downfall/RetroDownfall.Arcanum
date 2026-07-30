namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// RAG Phase 3 — contract for on-demand and background workspace file indexing (semantic codebase
/// retrieval). Defined in Core (rather than Infrastructure) so both the inference hub
/// (<c>WizardIntelligenceProvider</c>, in Api) and the manual re-index endpoint (in Api) can depend on
/// it without introducing an Infrastructure-to-Api project reference. The concrete implementation
/// (<c>WorkspaceIndexingService</c>) lives in Infrastructure alongside the raw-SQL indexing work itself.
/// </summary>
public interface IWorkspaceIndexingService
{

    /// <summary>
    /// Registers a workspace path as known for background re-indexing. Thread-safe; safe to call on
    /// every inference turn (<c>WizardIntelligenceProvider</c> does exactly this whenever
    /// <c>WorkingDirectory</c> is non-empty). The background service picks up new paths on its next
    /// tick — this method never performs I/O itself.
    /// </summary>
    void RegisterWorkspace(string workspacePath);

    /// <summary>
    /// Stops tracking an inactive workspace and disposes its watcher. Persisted chunks remain until
    /// explicit reset or a future registration/reconciliation.
    /// </summary>
    void UnregisterWorkspace(string workspacePath);

    /// <summary>
    /// Immediately indexes the given workspace path (used by the manual
    /// <c>POST /api/workspaces/{id}/files/index</c> re-index endpoint), awaiting completion. Never
    /// throws — errors are logged and swallowed, same graceful-degradation contract as a background
    /// tick. Also registers <paramref name="workspacePath"/> for future background ticks.
    /// </summary>
    Task IndexNowAsync(string workspacePath, CancellationToken cancellationToken);

}
