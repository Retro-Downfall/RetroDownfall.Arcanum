using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// MCP JSON-RPC transport: newline-delimited messages to a server and parsed inbound envelopes from the
/// server. Retained as a raw test-harness seam (see <see cref="InProcessMcpTransport"/>); production code
/// speaks to MCP servers exclusively through the ModelContextProtocol SDK now.
/// </summary>
internal interface IMcpTransport : IAsyncDisposable
{
    ChannelReader<McpInboundEnvelope> InboundReader { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task WriteRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default);

    Task WriteNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// Classifies an inbound JSON-RPC line read from an MCP server over the raw test-harness transport.
/// </summary>
public enum McpInboundKind
{
    Response,

    Notification,

    Request,
}

/// <summary>
/// One parsed inbound JSON-RPC message from the test-harness transport (newline-delimited).
/// </summary>
public readonly record struct McpInboundEnvelope(
    McpInboundKind Kind,
    JsonRpcResponse? Response,
    JsonRpcNotification? Notification,
    JsonRpcRequest? Request);
