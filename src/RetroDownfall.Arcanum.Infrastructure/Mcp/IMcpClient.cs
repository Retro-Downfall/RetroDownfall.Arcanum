using System.Text.Json;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Transport-agnostic MCP session client. Performs the <c>initialize</c> handshake, sends
/// JSON-RPC requests, and projects <c>tools/list</c> into <see cref="McpBridgeTool"/> instances.
/// Implemented by <see cref="McpClient"/> (stdio / in-process correlation transport) and
/// <see cref="McpHttpClient"/> (Streamable HTTP). <see cref="McpBridgeTool"/> and
/// <see cref="McpConnectionManager"/> depend on this abstraction so they stay transport-agnostic.
/// </summary>
internal interface IMcpClient : IAsyncDisposable
{
    /// <summary>
    /// Performs transport startup (if any) and the MCP <c>initialize</c> /
    /// <c>notifications/initialized</c> handshake. Must be called exactly once before
    /// <see cref="GetToolsAsync"/>.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a JSON-RPC request and awaits its <c>result</c> object. JSON-RPC <c>error</c>
    /// responses surface as <see cref="InvalidOperationException"/>; transport/connectivity
    /// failures surface as <see cref="McpTransportUnavailableException"/>. A null
    /// <paramref name="requestTimeout"/> uses the client's configured default;
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables the per-request timeout.
    /// </summary>
    Task<JsonElement> SendRequestAsync(
        string method,
        JsonElement? parameters,
        CancellationToken cancellationToken = default,
        TimeSpan? requestTimeout = null);

    /// <summary>
    /// Calls <c>tools/list</c> (paginated, capped) and maps each tool to a <see cref="McpBridgeTool"/>.
    /// </summary>
    Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(CancellationToken cancellationToken = default);
}
