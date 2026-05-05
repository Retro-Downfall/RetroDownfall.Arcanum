using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Parses one newline-delimited JSON-RPC line from an MCP server (stdio or in-process).
/// </summary>
internal static class McpInboundJsonRpc
{
    /// <summary>
    /// Parses one line of JSON using only source-generated <see cref="McpJsonSerializerContext"/> metadata.
    /// </summary>
    public static McpInboundEnvelope ParseInbound(string line, McpJsonSerializerContext json)
    {
        using JsonDocument doc = JsonDocument.Parse(line);

        return ParseInboundCore(doc.RootElement, json);
    }

    public static McpInboundEnvelope ParseInboundCore(JsonElement root, McpJsonSerializerContext json)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Root JSON value must be an object.");
        }

        bool hasMethod = root.TryGetProperty("method", out _);

        bool hasResult = root.TryGetProperty("result", out _);

        bool hasError = root.TryGetProperty("error", out _);

        bool hasId = root.TryGetProperty("id", out _);

        if (hasResult || hasError)
        {
            if (!hasId)
            {
                throw new JsonException("JSON-RPC response requires an \"id\" member.");
            }

            JsonRpcResponse? response = JsonSerializer.Deserialize(root, json.JsonRpcResponse);

            if (response is null)
            {
                throw new JsonException("Failed to deserialize JsonRpcResponse.");
            }

            return new McpInboundEnvelope(McpInboundKind.Response, response, null, null);
        }

        if (hasMethod && !hasResult && !hasError)
        {
            if (!hasId)
            {
                JsonRpcNotification? notification = JsonSerializer.Deserialize(root, json.JsonRpcNotification);

                if (notification is null)
                {
                    throw new JsonException("Failed to deserialize JsonRpcNotification.");
                }

                return new McpInboundEnvelope(McpInboundKind.Notification, null, notification, null);
            }

            JsonRpcRequest? request = JsonSerializer.Deserialize(root, json.JsonRpcRequest);

            if (request is null)
            {
                throw new JsonException("Failed to deserialize JsonRpcRequest.");
            }

            return new McpInboundEnvelope(McpInboundKind.Request, null, null, request);
        }

        throw new JsonException("Line is not a recognized JSON-RPC response, notification, or request shape.");
    }
}
