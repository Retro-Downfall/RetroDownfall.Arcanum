using System.Threading.Channels;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// MCP JSON-RPC transport: newline-delimited messages to a server and parsed inbound envelopes from the server.
/// </summary>
internal interface IMcpTransport : IAsyncDisposable
{
    ChannelReader<McpInboundEnvelope> InboundReader { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task WriteRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default);

    Task WriteNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default);
}
