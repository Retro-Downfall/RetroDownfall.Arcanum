using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Describes, from the host's own filesystem, what trusting a workspace-local <c>mcp.json</c> would allow.
/// </summary>
public interface IMcpWorkspaceTrustPreviewer
{
    /// <summary>
    /// Reads the <c>mcp.json</c> in <paramref name="workingDirectory"/> through the same secure, size-capped
    /// reader the trust store digests through, and describes it.
    /// </summary>
    /// <param name="workingDirectory">The workspace root on the host's machine.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The preview, or a typed failure: <c>Mcp.MissingWorkspace</c>, <c>Mcp.InvalidWorkspace</c>,
    /// <c>Mcp.MissingConfig</c> when there is no file, <c>Mcp.InvalidConfig</c> when it is not JSON the
    /// host can describe, and <c>Mcp.TrustFailed</c> when it is too large or not a safe regular file.
    /// </returns>
    Task<Result<McpWorkspaceTrustPreview>> PreviewAsync(
        string workingDirectory,
        CancellationToken cancellationToken = default);
}
