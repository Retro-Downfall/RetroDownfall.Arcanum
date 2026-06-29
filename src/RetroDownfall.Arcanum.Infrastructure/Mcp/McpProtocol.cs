using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Caps applied to a server's <c>tools/list</c> projection: pagination depth, per-server and
/// per-page tool counts, and a cumulative schema byte budget.
/// </summary>
internal readonly record struct McpToolListCaps(
    int MaxToolsListPages,
    int MaxToolsPerServer,
    int MaxToolsPerListPage,
    int MaxToolsTotalBytes);

/// <summary>
/// Transport-agnostic MCP protocol logic shared by <see cref="McpClient"/> (stdio / in-process)
/// and <see cref="McpHttpClient"/> (Streamable HTTP). Centralizes the <c>initialize</c> handshake
/// and the paginated <c>tools/list</c> → <see cref="McpBridgeTool"/> projection so the two client
/// implementations carry no duplicated protocol logic.
/// </summary>
internal static class McpProtocol
{

    /// <summary>MCP protocol version advertised by the stdio / in-process client.</summary>
    public const string StdioProtocolVersion = "2024-11-05";

    /// <summary>MCP Streamable HTTP protocol version (2026-07-28 spec).</summary>
    public const string StreamableHttpProtocolVersion = "2026-07-28";

    /// <summary>
    /// Builds the MCP <c>initialize</c> request params for the given <paramref name="protocolVersion"/>.
    /// </summary>
    public static McpInitializeParams CreateInitializeParams(string protocolVersion)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);

        return new McpInitializeParams
        {
            ProtocolVersion = protocolVersion,
            Capabilities = new McpClientCapabilities(),
            ClientInfo = new McpClientInfo
            {
                Name = typeof(McpProtocol).Assembly.GetName().Name ?? "RetroDownfall.Arcanum.Infrastructure",
                Version = typeof(McpProtocol).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            },
        };

    }

    /// <summary>
    /// Performs the shared MCP <c>initialize</c> handshake: sends the <c>initialize</c> request via
    /// <paramref name="client"/>, then dispatches <c>notifications/initialized</c> through the
    /// transport-specific <paramref name="sendInitializedNotificationAsync"/> callback.
    /// </summary>
    public static async Task InitializeAsync(
        IMcpClient client,
        string protocolVersion,
        Func<CancellationToken, Task> sendInitializedNotificationAsync,
        McpJsonSerializerContext json,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(client);

        ArgumentNullException.ThrowIfNull(sendInitializedNotificationAsync);

        ArgumentNullException.ThrowIfNull(json);

        McpInitializeParams initParams = CreateInitializeParams(protocolVersion);

        JsonElement initElement = JsonSerializer.SerializeToElement(initParams, json.McpInitializeParams);

        _ = await client.SendRequestAsync("initialize", initElement, cancellationToken, requestTimeout)
            .ConfigureAwait(false);

        await sendInitializedNotificationAsync(cancellationToken).ConfigureAwait(false);

    }

    /// <summary>
    /// Extracts the <c>result</c> from a JSON-RPC response, applying the shared error contract: an
    /// <c>error</c> object throws <see cref="InvalidOperationException"/>; a missing <c>result</c>
    /// also throws. Used by both client transports so error mapping never diverges.
    /// </summary>
    public static JsonElement ExtractResultOrThrow(JsonRpcResponse response)
    {

        ArgumentNullException.ThrowIfNull(response);

        if (response.Error is { } rpcError)
        {

            throw new InvalidOperationException(FormulateRpcErrorMessage(rpcError));

        }

        if (response.Result is not { } result)
        {

            throw new InvalidOperationException("JSON-RPC response missing result.");

        }

        return result;

    }

    /// <summary>
    /// Formats a JSON-RPC <c>error</c> object as a single human-readable message (<c>code: message [data]</c>).
    /// </summary>
    public static string FormulateRpcErrorMessage(JsonRpcError error)
    {

        ArgumentNullException.ThrowIfNull(error);

        string message = $"{error.Code}: {error.Message}";

        if (error.Data is { } data)
        {

            message += " " + data.GetRawText();

        }

        return message.Trim();

    }

    /// <summary>
    /// Calls <c>tools/list</c> with cursor pagination (de-duplicating cursors and enforcing
    /// <paramref name="caps"/>) and maps each accepted tool to a <see cref="McpBridgeTool"/> bound to
    /// <paramref name="client"/>.
    /// </summary>
    public static async Task<IReadOnlyList<McpBridgeTool>> GetToolsAsync(
        IMcpClient client,
        McpToolListCaps caps,
        long toolOutputCapBytes,
        McpJsonSerializerContext json,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(client);

        ArgumentNullException.ThrowIfNull(json);

        List<McpBridgeTool> tools = [];

        string? cursor = null;

        long totalToolBytes = 0L;

        HashSet<string> seenCursors = new(StringComparer.Ordinal);

        for (int page = 0; page < caps.MaxToolsListPages; page++)
        {

            if (cursor is not null)
            {

                if (!seenCursors.Add(cursor))
                {

                    break;

                }

            }

            if (tools.Count >= caps.MaxToolsPerServer)
            {

                break;

            }

            JsonElement? listParams = cursor is null
                ? null
                : JsonSerializer.SerializeToElement(new McpToolsListParams { Cursor = cursor }, json.McpToolsListParams);

            JsonElement pageResult = await client
                .SendRequestAsync("tools/list", listParams, cancellationToken)
                .ConfigureAwait(false);

            if (!pageResult.TryGetProperty("tools", out JsonElement toolsArray) ||
                toolsArray.ValueKind != JsonValueKind.Array)
            {

                break;

            }

            int toolsOnPage = 0;

            foreach (JsonElement tool in toolsArray.EnumerateArray())
            {

                if (tools.Count >= caps.MaxToolsPerServer)
                {

                    break;

                }

                if (toolsOnPage >= caps.MaxToolsPerListPage)
                {

                    break;

                }

                int toolUtf8Bytes = Encoding.UTF8.GetByteCount(tool.GetRawText());

                if (totalToolBytes + toolUtf8Bytes > caps.MaxToolsTotalBytes)
                {

                    break;

                }

                if (!tool.TryGetProperty("name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
                {

                    continue;

                }

                string? name = nameEl.GetString();

                if (string.IsNullOrWhiteSpace(name))
                {

                    continue;

                }

                string description = string.Empty;

                if (tool.TryGetProperty("description", out JsonElement descEl) &&
                    descEl.ValueKind == JsonValueKind.String)
                {

                    description = McpSecurityLimits.BoundToolDescription(descEl.GetString() ?? string.Empty);

                }

                JsonElement inputSchema;

                if (tool.TryGetProperty("inputSchema", out JsonElement schemaEl) &&
                    schemaEl.ValueKind == JsonValueKind.Object)
                {

                    inputSchema = McpSecurityLimits.BoundToolInputSchema(schemaEl, json);

                }
                else
                {

                    inputSchema = JsonSerializer.SerializeToElement(new McpEmptyJsonObject(), json.McpEmptyJsonObject);

                }

                tools.Add(new McpBridgeTool(name, description, inputSchema, client, toolOutputCapBytes));

                totalToolBytes += toolUtf8Bytes;

                toolsOnPage++;

            }

            if (!pageResult.TryGetProperty("nextCursor", out JsonElement next) ||
                next.ValueKind != JsonValueKind.String)
            {

                break;

            }

            cursor = next.GetString();

            if (string.IsNullOrEmpty(cursor))
            {

                break;

            }

        }

        return tools;

    }

}
