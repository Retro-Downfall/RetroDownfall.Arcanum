namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Persists operator-approved workspace-local <c>mcp.json</c> configurations (path + content hash).
/// </summary>
public interface ITrustedMcpWorkspaceStore
{

    Task<bool> IsTrustedAsync(string workspaceRootPath, CancellationToken cancellationToken = default);

    Task<bool> IsTrustedAsync(
        string workspaceRootPath,
        string sourceDigest,
        CancellationToken cancellationToken = default);

    Task<bool> IsApprovedDigestAsync(
        string workspaceRootPath,
        string sourceDigest,
        CancellationToken cancellationToken = default);

    Task<TrustedMcpWorkspaceSnapshot> GetSnapshotAsync(
        string workspaceRootPath,
        CancellationToken cancellationToken = default);

    Task TrustAsync(string workspaceRootPath, CancellationToken cancellationToken = default);

}

public readonly record struct TrustedMcpWorkspaceSnapshot(
    string? CurrentDigest,
    bool IsApproved)
{

    public bool Authorizes(string? sourceDigest) =>
        IsApproved
        && sourceDigest is not null
        && string.Equals(
            CurrentDigest,
            sourceDigest,
            StringComparison.OrdinalIgnoreCase);

}
