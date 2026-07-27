using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Intelligence;
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

        string requestId = request.Id.ToString();
        BindRequestContexts(connectionKey, requestId);

        if (SessionAttachmentToolAmbient.CurrentSessionId is not Guid sessionId)
        {
            return;
        }

        if (!string.IsNullOrEmpty(requestId))
        {
            SessionAttachmentToolAmbient.BindRequest(connectionKey, requestId, sessionId);

            return;
        }

        string token = SessionAttachmentToolAmbient.CreateAndBindOpaqueToken(sessionId);

        request.Params = InjectOpaqueTokenIntoSdkParams(request.Params, token);

    }

    public static void UnbindFailedSdkToolsCall(
        string connectionKey,
        JsonRpcMessage message)
    {

        if (message is not JsonRpcRequest { Method: "tools/call" } request)
        {

            return;
        }

        string requestId = request.Id.ToString();
        UnbindRequestContexts(connectionKey, requestId);
    }

    public static void MarkSdkToolsCallDispatched(
        string connectionKey,
        JsonRpcMessage message)
    {
        if (message is not JsonRpcRequest
            {
                Method: "tools/call",
            } request)
        {
            return;
        }

        MarkApplyPatchDispatched(
            connectionKey,
            request.Id.ToString());
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

        string requestId = NormalizeArcanumRequestId(request.Id);
        BindRequestContexts(connectionKey, requestId);

        if (SessionAttachmentToolAmbient.CurrentSessionId is not Guid sessionId)
        {
            return request;
        }

        if (!string.IsNullOrEmpty(requestId))
        {
            SessionAttachmentToolAmbient.BindRequest(connectionKey, requestId, sessionId);

            return request;
        }

        string token = SessionAttachmentToolAmbient.CreateAndBindOpaqueToken(sessionId);

        return request with { Params = InjectOpaqueTokenIntoArcanumParams(request.Params, token) };

    }

    public static void UnbindFailedToolsCall(
        string connectionKey,
        ArcanumJsonRpcRequest request)
    {

        if (!string.Equals(
                request.Method,
                "tools/call",
                StringComparison.Ordinal))
        {

            return;
        }

        string requestId = NormalizeArcanumRequestId(request.Id);
        UnbindRequestContexts(connectionKey, requestId);
    }

    public static void MarkToolsCallDispatched(
        string connectionKey,
        ArcanumJsonRpcRequest request)
    {
        if (!string.Equals(
                request.Method,
                "tools/call",
                StringComparison.Ordinal))
        {
            return;
        }

        MarkApplyPatchDispatched(
            connectionKey,
            NormalizeArcanumRequestId(request.Id));
    }

    private static void BindRequestContexts(
        string connectionKey,
        string requestId)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        if (WorkspaceCheckInferenceDeadlineAmbient.CurrentDeadlineTimestamp
            is long deadline)
        {
            WorkspaceCheckDeadlineBinding.BindRequest(
                connectionKey,
                requestId,
                deadline);
        }

        if (ApplyPatchInvocationAmbient.Current
            is ApplyPatchInvocationContext patchContext)
        {
            ApplyPatchInvocationBinding.BindRequest(
                connectionKey,
                requestId,
                patchContext);
        }

        if (PersistedToolInvocationAmbient.Current
            is PersistedToolInvocationContext persistedContext)
        {
            PersistedToolInvocationBinding.BindRequest(
                connectionKey,
                requestId,
                persistedContext);
        }
    }

    private static void UnbindRequestContexts(
        string connectionKey,
        string requestId)
    {
        SessionAttachmentToolAmbient.UnbindRequest(
            connectionKey,
            requestId);
        WorkspaceCheckDeadlineBinding.UnbindRequest(
            connectionKey,
            requestId);
        ApplyPatchInvocationBinding.UnbindRequest(
            connectionKey,
            requestId);
        PersistedToolInvocationBinding.UnbindRequest(
            connectionKey,
            requestId);
    }

    private static void MarkApplyPatchDispatched(
        string connectionKey,
        string requestId)
    {
        if (ApplyPatchInvocationBinding.TryResolveRequest(
                connectionKey,
                requestId,
                out ApplyPatchInvocationContext? context)
            && context is not null)
        {
            context.MarkDispatched();
        }
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
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            bool wroteArguments = false;

            if (paramsElement is { ValueKind: JsonValueKind.Object } pe)
            {
                foreach (JsonProperty property in pe.EnumerateObject())
                {
                    if (string.Equals(property.Name, "arguments", StringComparison.Ordinal))
                    {
                        WriteArgumentsObjectWithToken(writer, property.Value, token);
                        wroteArguments = true;
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
            }

            if (!wroteArguments)
            {
                WriteArgumentsObjectWithToken(writer, arguments: default, token);
            }

            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());

        return document.RootElement.Clone();
    }

    private static void WriteArgumentsObjectWithToken(Utf8JsonWriter writer, JsonElement arguments, string token)
    {
        writer.WritePropertyName("arguments");
        writer.WriteStartObject();

        if (arguments.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty arg in arguments.EnumerateObject())
            {
                if (string.Equals(
                        arg.Name,
                        SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                arg.WriteTo(writer);
            }
        }

        writer.WriteString(SessionAttachmentToolAmbient.OpaqueInvocationTokenArgumentName, token);
        writer.WriteEndObject();
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
