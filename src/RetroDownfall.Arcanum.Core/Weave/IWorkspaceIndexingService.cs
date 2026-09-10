using RetroDownfall.Arcanum.Core.Primitives;

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
    /// tick. Registration validates the directory and may establish its bounded file watcher.
    /// </summary>
    void RegisterWorkspace(string workspacePath);

    /// <summary>
    /// Stops tracking an inactive workspace and disposes its watcher. Persisted chunks remain until
    /// explicit reset or a future registration/reconciliation.
    /// </summary>
    void UnregisterWorkspace(string workspacePath);

    /// <summary>
    /// Resolves a live scheduler alias to its immutable persisted indexing path. Readers resolve
    /// once and use this exact spelling for ranking and metadata joins. Unregistered paths retain
    /// ordinary full-path normalization; persisted workspace and chunk identities are not rewritten.
    /// </summary>
    string ResolveIndexedWorkspacePath(string workspacePath);

    /// <summary>
    /// Accepts a full reconciliation into the producer-owned scheduler, or coalesces it with
    /// existing demand. Capacity overflow remains accepted data; only closed/unavailable intake
    /// fails with <see cref="ErrorCodes.Workspace.IndexingUnavailable"/>. Accepted work is owned
    /// by the host, independently of the requesting connection's lifetime.
    /// </summary>
    Result<WorkspaceIndexQueueDisposition> QueueIndexNow(string workspacePath);
}

/// <summary>The immediate disposition of an accepted workspace reconciliation request.</summary>
public enum WorkspaceIndexQueueDisposition
{
    Accepted,
    Coalesced,
}
