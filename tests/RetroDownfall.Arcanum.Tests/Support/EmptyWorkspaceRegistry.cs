using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// A workspace registry that knows no workspace: what the offline-maintenance composition supplies, so
/// every claimed workspace root is unregistered and the attachment source resolver refuses it.
/// </summary>
internal sealed class EmptyWorkspaceRegistry : IWorkspaceRegistry
{
    public Task<WorkspaceInfo[]> GetAllAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<WorkspaceInfo[]>([]);
    }

    public Task<WorkspaceInfo?> GetAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult<WorkspaceInfo?>(null);
    }

    public Task<Result<WorkspaceInfo>> RegisterAsync(CreateWorkspaceRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Result<WorkspaceInfo>> UpdateAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Result<bool>> UnregisterAsync(string id, CancellationToken ct) =>
        throw new NotSupportedException();
}
