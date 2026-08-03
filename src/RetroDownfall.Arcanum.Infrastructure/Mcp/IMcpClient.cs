using ModelContextProtocol.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Arcanum's session seam over one MCP server connection. The only implementation is
/// <see cref="SdkMcpClientWrapper"/>, which delegates the <c>initialize</c> handshake,
/// <c>tools/list</c>, and <c>tools/call</c> to the official ModelContextProtocol SDK.
/// <see cref="McpBridgeTool"/> and <see cref="McpConnectionManager"/> depend on this narrow
/// abstraction (not the SDK client directly) so the registry, merge, and fallback logic stay
/// transport- and SDK-detail agnostic.
/// </summary>
internal interface IMcpClient : IAsyncDisposable
{
    /// <summary>
    /// Connects the underlying transport and performs the MCP <c>initialize</c> handshake. Must be
    /// called exactly once before <see cref="GetToolsAsync"/> or <see cref="CallToolAsync"/>.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <c>tools/list</c> (paginated, capped) and maps each accepted tool to a <see cref="McpBridgeTool"/>.
    /// </summary>
    Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Calls <c>tools/call</c> for <paramref name="toolName"/>. A null <paramref name="requestTimeout"/>
    /// uses the client's configured default; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// disables the per-request timeout (used for human prompts and child-process tools). Transport/connectivity
    /// failures surface as <see cref="McpTransportUnavailableException"/> with conservative
    /// dispatch classification; fallback is allowed only when no dispatch occurred. A tool-level
    /// error (<c>isError: true</c>) is returned normally on <see cref="CallToolResult.IsError"/>.
    /// </summary>
    Task<CallToolResult> CallToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default);
}
