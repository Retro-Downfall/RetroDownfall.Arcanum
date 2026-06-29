using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Infrastructure.CommLink;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Wire mechanics for the MCP Streamable HTTP transport (2026-07-28 spec): POSTs a single
/// JSON-RPC frame with the mandatory <c>MCP-Protocol-Version</c>, <c>Mcp-Method</c>, and
/// <c>Mcp-Name</c> headers and an <c>Accept: application/json, text/event-stream</c> negotiation,
/// then parses either a single JSON response or an SSE stream (collecting notifications and
/// returning the final JSON-RPC response). Stateless: no <c>Mcp-Session-Id</c> is sent or tracked.
/// </summary>
internal static class McpHttpTransport
{

    public const string ProtocolVersionHeader = "MCP-Protocol-Version";

    public const string MethodHeader = "Mcp-Method";

    public const string NameHeader = "Mcp-Name";

    private const string JsonMediaType = "application/json";

    private const string EventStreamMediaType = "text/event-stream";

    private const string DataFieldPrefix = "data:";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// POSTs a JSON-RPC request and returns the live response (headers read; body not yet
    /// consumed, so SSE streaming and stream-close cancellation both work). Enforces the outbound
    /// line cap before any byte leaves the process.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        Uri endpoint,
        JsonRpcRequest request,
        string mcpName,
        McpJsonSerializerContext json,
        int maxJsonRpcLineBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(httpClient);

        ArgumentNullException.ThrowIfNull(endpoint);

        ArgumentNullException.ThrowIfNull(request);

        ArgumentNullException.ThrowIfNull(json);

        string body = JsonSerializer.Serialize(request, json.JsonRpcRequest);

        McpOutboundLineGuard.Enforce(body, maxJsonRpcLineBytes);

        using HttpRequestMessage httpRequest = BuildPost(endpoint, request.Method, mcpName, body);

        return await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// POSTs a JSON-RPC notification. Per the 2026-07-28 spec the server replies <c>202 Accepted</c>
    /// with no body; any non-success status surfaces as <see cref="McpTransportUnavailableException"/>.
    /// </summary>
    public static async Task SendNotificationAsync(
        HttpClient httpClient,
        Uri endpoint,
        JsonRpcNotification notification,
        McpJsonSerializerContext json,
        int maxJsonRpcLineBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(httpClient);

        ArgumentNullException.ThrowIfNull(endpoint);

        ArgumentNullException.ThrowIfNull(notification);

        ArgumentNullException.ThrowIfNull(json);

        string body = JsonSerializer.Serialize(notification, json.JsonRpcNotification);

        McpOutboundLineGuard.Enforce(body, maxJsonRpcLineBytes);

        using HttpRequestMessage httpRequest = BuildPost(endpoint, notification.Method, notification.Method, body);

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        await HttpResponseBodyDrainer.DrainAsync(response.Content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {

            throw new McpTransportUnavailableException(
                $"MCP HTTP notification POST returned HTTP {(int)response.StatusCode}.");

        }

    }

    /// <summary>
    /// Parses a successful HTTP response into a JSON-RPC response. A <c>text/event-stream</c> body is
    /// read incrementally (notifications collected and discarded, the final response returned); any
    /// other body is parsed as a single JSON-RPC response.
    /// </summary>
    public static async Task<JsonRpcResponse> ParseResponseAsync(
        HttpResponseMessage response,
        McpJsonSerializerContext json,
        int maxJsonRpcLineBytes,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(response);

        ArgumentNullException.ThrowIfNull(json);

        string? mediaType = response.Content.Headers.ContentType?.MediaType;

        if (string.Equals(mediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase))
        {

            return await ParseEventStreamAsync(response, json, maxJsonRpcLineBytes, cancellationToken).ConfigureAwait(false);

        }

        return await ParseJsonAsync(response, json, maxJsonRpcLineBytes, cancellationToken).ConfigureAwait(false);

    }

    private static HttpRequestMessage BuildPost(Uri endpoint, string method, string mcpName, string body)
    {

        HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Utf8NoBom, JsonMediaType),
        };

        httpRequest.Headers.TryAddWithoutValidation(ProtocolVersionHeader, McpProtocol.StreamableHttpProtocolVersion);

        httpRequest.Headers.TryAddWithoutValidation(MethodHeader, method);

        httpRequest.Headers.TryAddWithoutValidation(NameHeader, string.IsNullOrWhiteSpace(mcpName) ? method : mcpName);

        httpRequest.Headers.Accept.ParseAdd(JsonMediaType);

        httpRequest.Headers.Accept.ParseAdd(EventStreamMediaType);

        return httpRequest;

    }

    private static async Task<JsonRpcResponse> ParseJsonAsync(
        HttpResponseMessage response,
        McpJsonSerializerContext json,
        int maxJsonRpcLineBytes,
        CancellationToken cancellationToken)
    {

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        string body = await ReadCappedTextAsync(stream, maxJsonRpcLineBytes, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(body))
        {

            throw new McpTransportUnavailableException("MCP HTTP response body was empty.");

        }

        McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(body, json, maxJsonRpcLineBytes);

        if (envelope.Kind != McpInboundKind.Response || envelope.Response is null)
        {

            throw new McpTransportUnavailableException("MCP HTTP response body was not a JSON-RPC response.");

        }

        return envelope.Response;

    }

    private static async Task<JsonRpcResponse> ParseEventStreamAsync(
        HttpResponseMessage response,
        McpJsonSerializerContext json,
        int maxJsonRpcLineBytes,
        CancellationToken cancellationToken)
    {

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        using StreamReader streamReader = new(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        McpStdioLineReader lineReader = new(streamReader);

        StringBuilder dataBuilder = new();

        int dataLineCount = 0;

        long eventBytes = 0L;

        JsonRpcResponse? finalResponse = null;

        while (true)
        {

            string? line;

            try
            {

                line = await lineReader.ReadLineUtf8CappedAsync(maxJsonRpcLineBytes, cancellationToken).ConfigureAwait(false);

            }
            catch (JsonException)
            {

                throw new McpLineSizeExceededException(maxJsonRpcLineBytes, maxJsonRpcLineBytes + 1);

            }

            if (line is null)
            {

                finalResponse = TryParseEventResponse(dataBuilder, dataLineCount, json, maxJsonRpcLineBytes) ?? finalResponse;

                break;

            }

            if (line.Length == 0)
            {

                JsonRpcResponse? parsed = TryParseEventResponse(dataBuilder, dataLineCount, json, maxJsonRpcLineBytes);

                dataBuilder.Clear();

                dataLineCount = 0;

                eventBytes = 0L;

                if (parsed is not null)
                {

                    finalResponse = parsed;

                    break;

                }

                continue;

            }

            if (line[0] == ':')
            {

                continue;

            }

            if (!TryGetDataPayload(line, out string payload))
            {

                continue;

            }

            int payloadBytes = Encoding.UTF8.GetByteCount(payload);

            if (eventBytes + payloadBytes > maxJsonRpcLineBytes)
            {

                throw new McpLineSizeExceededException(
                    maxJsonRpcLineBytes,
                    (int)Math.Min(int.MaxValue, eventBytes + payloadBytes));

            }

            if (dataLineCount > 0)
            {

                dataBuilder.Append('\n');

            }

            dataBuilder.Append(payload);

            eventBytes += payloadBytes;

            dataLineCount++;

        }

        if (finalResponse is null)
        {

            throw new McpTransportUnavailableException("MCP HTTP event stream ended without a JSON-RPC response.");

        }

        return finalResponse;

    }

    private static JsonRpcResponse? TryParseEventResponse(
        StringBuilder dataBuilder,
        int dataLineCount,
        McpJsonSerializerContext json,
        int maxJsonRpcLineBytes)
    {

        if (dataLineCount == 0)
        {

            return null;

        }

        string payload = dataBuilder.ToString();

        if (string.IsNullOrWhiteSpace(payload))
        {

            return null;

        }

        try
        {

            McpInboundEnvelope envelope = McpInboundJsonRpc.ParseInbound(payload, json, maxJsonRpcLineBytes);

            // Notifications and server-originated requests are drained; only a response terminates the stream.
            return envelope.Kind == McpInboundKind.Response ? envelope.Response : null;

        }
        catch (JsonException)
        {

            // Tolerate keep-alive or non-JSON-RPC data events without aborting the stream.
            return null;

        }

    }

    private static bool TryGetDataPayload(string line, out string payload)
    {

        if (!line.StartsWith(DataFieldPrefix, StringComparison.Ordinal))
        {

            payload = string.Empty;

            return false;

        }

        string rest = line[DataFieldPrefix.Length..];

        if (rest.StartsWith(' '))
        {

            rest = rest[1..];

        }

        payload = rest;

        return true;

    }

    private static async Task<string> ReadCappedTextAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {

        int capacity = maxBytes + 1;

        byte[] rented = ArrayPool<byte>.Shared.Rent(capacity);

        try
        {

            int total = 0;

            while (total < capacity)
            {

                int read = await stream
                    .ReadAsync(rented.AsMemory(total, capacity - total), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0)
                {

                    break;

                }

                total += read;

            }

            if (total > maxBytes)
            {

                throw new McpLineSizeExceededException(maxBytes, total);

            }

            return Encoding.UTF8.GetString(rented, 0, total);

        }
        finally
        {

            ArrayPool<byte>.Shared.Return(rented);

        }

    }

}
