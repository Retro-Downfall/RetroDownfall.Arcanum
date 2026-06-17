using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces;

public interface IWorkspaceRegistry
{

    Task<WorkspaceInfo[]> GetAllAsync(CancellationToken ct);

    Task<WorkspaceInfo?> GetAsync(string id, CancellationToken ct);

    Task<Result<WorkspaceInfo>> RegisterAsync(CreateWorkspaceRequest request, CancellationToken ct);

    Task<Result<WorkspaceInfo>> UpdateAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct);

    Task<Result<bool>> UnregisterAsync(string id, CancellationToken ct);

}
