namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Persists operator-approved workspace-local <c>mcp.json</c> configurations (path + content hash).
/// </summary>
public interface ITrustedMcpWorkspaceStore
{

    Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default);

    Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default);

}
