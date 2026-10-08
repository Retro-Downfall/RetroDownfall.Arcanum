namespace RetroDownfall.Arcanum.Core.Mcp;

/// <summary>
/// Thrown by <see cref="ITrustedMcpWorkspaceStore.TrustAsync"/> when the <c>mcp.json</c> it read is not the
/// one the operator was shown: the digest of the bytes it read differs from the expected digest.
/// </summary>
public sealed class McpWorkspaceConfigChangedException : InvalidOperationException
{
    public McpWorkspaceConfigChangedException()
        : base("The workspace mcp.json changed after it was previewed.")
    {
    }

    public McpWorkspaceConfigChangedException(string message)
        : base(message)
    {
    }

    public McpWorkspaceConfigChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
