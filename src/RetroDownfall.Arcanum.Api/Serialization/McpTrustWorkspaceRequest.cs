namespace RetroDownfall.Arcanum.Api.Serialization;

/// <summary>
/// JSON body of <c>POST /api/mcp/trust-workspace</c>: the workspace whose <c>mcp.json</c> to trust and,
/// optionally, the digest of the preview the operator approved.
/// </summary>
/// <param name="WorkingDirectory">The workspace root on the host's machine.</param>
/// <param name="ExpectedConfigDigest">
/// The <c>configDigest</c> of the preview the operator approved
/// (<c>POST /api/mcp/trust-workspace/preview</c>). When present, the host trusts the file only if the bytes
/// it reads now have that digest, and otherwise refuses with <c>Mcp.ConfigChanged</c> and records nothing.
/// When absent, the host trusts whatever the file holds when it reads it, which is what a caller that never
/// showed an operator the file gets.
/// </param>
public sealed record McpTrustWorkspaceRequest(
    string? WorkingDirectory = null,
    string? ExpectedConfigDigest = null);
