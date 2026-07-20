using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Storage;
using ArcanumJsonRpcRequest = RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol.JsonRpcRequest;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Binds <see cref="SessionAttachmentToolAmbient.CurrentSessionId"/> to an outgoing internal MCP
/// <c>tools/call</c> at the client send boundary (JSON-RPC request id preferred; opaque token fallback).
/// </summary>
internal static class SessionAttachmentAmbientSend
{

    /// <summary>
    /// SDK transport path: bind by <see cref="JsonRpcRequest.Id"/> or inject an opaque token into params.
    /// </summary>
    public static void BindSdkToolsCall(string connectionKey, JsonRpcMessage message)
    {

        if (message is not JsonRpcRequest { Method: "tools/call" } request)
        {
            return;
        }

        if (SessionAttachmentToolAmbient.CurrentSessionId is not Guid sessionId)
        {
            return;
        }

        string requestId = request.Id.ToString();

        if (!string.IsNullOrEmpty(requestId))
        {
            SessionAttachmentToolAmbient.BindRequest(connectionKey, requestId, sessionId);

            return;
        }

        string token = SessionAttachmentToolAmbient.CreateAndBindOpaqueToken(sessionId);

        request.Params = InjectOpaqueTokenIntoSdkParams(request.Params, token);

    }

    /// <summary>
    /// Raw in-process test harness path: bind by request id or inject opaque token into params.
    /// </summary>
    public static ArcanumJsonRpcRequest ApplyAmbientBinding(string connectionKey, ArcanumJsonRpcRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Method, "tools/call", StringComparison.Ordinal))
        {
            return request;
        }

        if (SessionAttachmentToolAmbient.CurrentSessionId is not Guid sessionId)
        {
            return request;
        }

        string requestId = NormalizeArcanumRequestId(request.Id);

        if (!string.IsNullOrEmpty(requestId))
        {
            SessionAttachmentToolAmbient.BindRequest(connectionKey, requestId, sessionId);

            return request;
        }

        string token = SessionAttachmentToolAmbient.CreateAndBindOpaqueToken(sessionId);

        return request with { Params = InjectOpaqueTokenIntoArcanumParams(request.Params, token) };

    }

    /// <summary>
    /// Test helper: bind session via opaque token only (skips request-id map) so strip/audit
    /// assertions can cover the fallback path while still using a normal request id for the response.
    /// </summary>
    internal static ArcanumJsonRpcRequest ApplyOpaqueTokenBindingOnly(ArcanumJsonRpcRequest request)
    {

        ArgumentNullException.ThrowIfNull(request);

        if (SessionAttachmentToolAmbient.CurrentSessionId is not Guid sessionId)
        {
            return request;
        }

        string token = SessionAttachmentToolAmbient.CreateAndBindOpaqueToken(sessionId);

        return request with { Params = InjectOpaqueTokenIntoArcanumParams(request.Params, token) };

    }

    private static JsonNode? InjectOpaqueTokenIntoSdkParams(JsonNode? paramsNode, string token)
    {

        JsonObject root = paramsNode as JsonObject ?? [];

        JsonObject args = root["arguments"] as JsonObject ?? [];

        // Overwrite any model-supplied value.
        args[SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName] = JsonValue.Create(token);

        root["arguments"] = args;

        return root;

    }

    private static JsonElement InjectOpaqueTokenIntoArcanumParams(JsonElement? paramsElement, string token)
    {

        Dictionary<string, JsonElement> root = new(StringComparer.Ordinal);

        Dictionary<string, JsonElement> args = new(StringComparer.Ordinal);

        if (paramsElement is { ValueKind: JsonValueKind.Object } pe)
        {
            foreach (JsonProperty property in pe.EnumerateObject())
            {
                if (string.Equals(property.Name, "arguments", StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty arg in property.Value.EnumerateObject())
                    {
                        if (string.Equals(
                                arg.Name,
                                SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        args[arg.Name] = arg.Value.Clone();
                    }
                }
                else
                {
                    root[property.Name] = property.Value.Clone();
                }
            }
        }

        args[SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName] =
            JsonSerializer.SerializeToElement(token);

        root["arguments"] = JsonSerializer.SerializeToElement(args);

        return JsonSerializer.SerializeToElement(root);

    }

    private static string NormalizeArcanumRequestId(JsonElement? id)
    {

        if (id is not { } element)
        {
            return string.Empty;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            _ => string.Empty,
        };

    }

}
