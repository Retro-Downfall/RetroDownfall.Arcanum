using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using RetroDownfall.Arcanum.Core.Covenant;
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
        BindRequestContexts(connectionKey, requestId, ReadSdkToolName(request.Params));

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
        BindRequestContexts(connectionKey, requestId, ReadArcanumToolName(request.Params));

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
        string requestId,
        string? toolName)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        BindCovenantStaging(connectionKey, requestId, toolName);

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

        if (ApprenticeToolInvocationAmbient.Current
            is ApprenticeToolInvocationContext { IsValid: true } apprenticeContext)
        {
            // This boundary runs inside the turn's async flow, which is the only place the turn's budget
            // reservation is visible — the in-process server runs on its own task (issue #69).
            ApprenticeToolInvocationBinding.BindRequest(
                connectionKey,
                requestId,
                apprenticeContext with
                {
                    BudgetReservationId = DelegatedSpendAttribution.BudgetReservationId,
                });
        }
    }

    /// <summary>
    /// Mints this tool call's single-use Covenant capability, or mints nothing.
    /// </summary>
    /// <remarks>
    /// One capability per tool call, bound to the exact tool name and request identity, because a
    /// capability minted per turn would authorize whatever arrived rather than what the turn planned
    /// for. <c>TryAdd</c> semantics mean a duplicate request id is refused rather than overwriting a
    /// live registration.
    ///
    /// <para>A retirement's capability additionally requires the exact canonical preflight. It arrives
    /// on the staging context for the one dispatch it authorizes; a retirement that reaches here
    /// without it mints nothing, and the handler then fails closed exactly as it does for a turn that
    /// carries no capability at all.</para>
    /// </remarks>
    private static void BindCovenantStaging(
        string connectionKey,
        string requestId,
        string? toolName)
    {

        if (toolName is not { Length: > 0 } name
            || !CovenantToolNames.IsCovenantMutationTool(name)
            || CovenantToolStagingAmbient.Current is not { } staging)
        {
            return;
        }

        bool retirement = string.Equals(name, CovenantToolNames.RetireCovenant, StringComparison.Ordinal);

        if (!retirement && !staging.CanStageProposal)
        {
            return;
        }

        if (retirement != (staging.RetirementPreflight is not null))
        {

            // A proposal carrying retirement material, or a retirement carrying none, is a capability
            // the constructor would refuse anyway. Refusing here keeps the mint from ever producing one
            // whose shape contradicts the tool it authorizes.
            return;

        }

        // The nonce the disclosure receipt already bound one frame earlier. One call is one nonce.
        CovenantToolCapabilityNonce nonce = staging.Nonce ?? CovenantToolCapabilityNonce.Create();

        CovenantToolInvocationContext capability;

        try
        {
            capability = new CovenantToolInvocationContext(
                staging.Collector,
                staging.Campaign,
                staging.ProducingAdmission,
                staging.Materialization,
                staging.HeadProbe,
                nonce,
                name,
                requestId,
                staging.RetirementPreflight,
                staging.TurnCancellation);
        }
        catch (ArgumentException)
        {
            // The capability's own invariants refused this turn's material — an unbound Campaign, a
            // plan the collector does not match. Mint nothing; the handler then fails closed on a
            // missing capability, which is the same answer with none of the pretence.
            return;
        }

        _ = staging.Registry.TryRegister(connectionKey, requestId, capability, nonce);

    }

    private static void UnbindRequestContexts(
        string connectionKey,
        string requestId)
    {
        // BindCovenantStaging (above) only ever registers against the registry the turn's own
        // staging carries, and unbind runs on the same request's send-then-fail path, so the
        // ambient here is still the staging that minted whatever was registered for this id — if
        // anything was. TryRegister is TryAdd-only, so a frame that never reached the wire would
        // otherwise strand the id for the rest of the connection.
        if (CovenantToolStagingAmbient.Current is { } staging)
        {
            _ = staging.Registry.ReleaseUnsent(connectionKey, requestId);
        }

        SessionAttachmentToolAmbient.UnbindRequest(
            connectionKey,
            requestId);
        ApplyPatchInvocationBinding.UnbindRequest(
            connectionKey,
            requestId);
        PersistedToolInvocationBinding.UnbindRequest(
            connectionKey,
            requestId);
        ApprenticeToolInvocationBinding.UnbindRequest(
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

    /// <summary>Reads the tool name a <c>tools/call</c> names, or null when it names none.</summary>
    private static string? ReadSdkToolName(JsonNode? paramsNode) =>
        paramsNode is JsonObject root && root.TryGetPropertyValue("name", out JsonNode? name)
            ? name?.GetValue<string>()
            : null;

    private static string? ReadArcanumToolName(JsonElement? paramsElement) =>
        paramsElement is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("name", out JsonElement name)
            && name.ValueKind is JsonValueKind.String
            ? name.GetString()
            : null;

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
