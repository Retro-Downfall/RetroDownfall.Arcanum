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

    /// <summary>
    /// Reads the workspace's <c>mcp.json</c> once and records approval of exactly those bytes.
    /// </summary>
    /// <param name="workspaceRootPath">The workspace root.</param>
    /// <param name="expectedSourceDigest">
    /// The SHA-256 (hex) the operator was shown, or <see langword="null"/> to approve whatever is read. When
    /// supplied it is compared with the digest of the bytes this call read, and a mismatch records nothing and
    /// throws <see cref="McpWorkspaceConfigChangedException"/>, so no change can slip in between the preview
    /// and the approval.
    /// </param>
    /// <param name="cancellationToken">Cancels the approval.</param>
    Task TrustAsync(
        string workspaceRootPath,
        string? expectedSourceDigest = null,
        CancellationToken cancellationToken = default);
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
