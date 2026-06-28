using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Thrown when an outbound JSON-RPC line exceeds <see cref="McpSecurityLimits"/>'s
/// <c>MaxJsonRpcLineBytes</c> cap, before any byte is written to the transport. Distinct
/// from a transport-unavailable failure: this is a caller-side payload limit, not a
/// connectivity fault, so it must NOT trigger a fallback to another server.
/// </summary>
internal sealed class McpLineSizeExceededException : Exception
{

    public int MaxJsonRpcLineBytes { get; }

    public int ActualUtf8Bytes { get; }

    public McpLineSizeExceededException(int maxJsonRpcLineBytes, int actualUtf8Bytes)
        : base($"Outbound JSON-RPC line of {actualUtf8Bytes} UTF-8 bytes exceeds the maximum of {maxJsonRpcLineBytes} bytes; the line was not written.")
    {

        MaxJsonRpcLineBytes = maxJsonRpcLineBytes;

        ActualUtf8Bytes = actualUtf8Bytes;

    }

}

/// <summary>
/// Thrown when an MCP transport is unavailable (the local server is down, unreachable, or
/// the connection closed before a response was received). Distinct from a tool-execution
/// failure (a <c>tools/call</c> that returned an error or threw after the tool ran): only
/// this category is permitted to trigger the global fallback in <see cref="McpBridgeTool"/>,
/// because re-running a possibly-mutating tool on a fallback server is unsafe once the tool
/// has already executed on the local server.
/// </summary>
internal sealed class McpTransportUnavailableException : Exception
{

    public McpTransportUnavailableException(string message)
        : base(message)
    {

    }

    public McpTransportUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {

    }

}

/// <summary>
/// Outbound line-size enforcement shared by the MCP transports. Centralizes the
/// pre-write cap check so <see cref="InProcessMcpTransport"/> and <see cref="McpProcessTransport"/>
/// cannot diverge, and so the check happens AFTER source-generated serialization but BEFORE
/// any byte is written to the wire/channel.
/// </summary>
internal static class McpOutboundLineGuard
{

    /// <summary>
    /// Throws <see cref="McpLineSizeExceededException"/> when <paramref name="line"/> exceeds
    /// <paramref name="maxJsonRpcLineBytes"/> UTF-8 bytes. AOT-safe: <paramref name="line"/> is
    /// already the source-generated serialized string from a <see cref="JsonSerializerContext"/>.
    /// </summary>
    public static void Enforce(string line, int maxJsonRpcLineBytes)
    {

        if (McpSecurityLimits.ExceedsMaxLineUtf8Bytes(line, maxJsonRpcLineBytes))
        {

            int actual = Encoding.UTF8.GetByteCount(line);

            throw new McpLineSizeExceededException(maxJsonRpcLineBytes, actual);

        }

    }

}
