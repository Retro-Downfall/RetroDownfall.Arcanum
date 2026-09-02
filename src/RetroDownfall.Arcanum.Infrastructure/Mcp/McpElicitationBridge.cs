using System.Text.Json;
using ModelContextProtocol.Client;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Infrastructure.Mcp.Protocol;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Bridges a server's MCP <c>elicitation/create</c> request to the same <see cref="IHumanPromptRegistry"/>
/// channel the in-process <c>ask_human</c> tool uses. <see cref="McpConnectionManager"/> registers the
/// handler on every SDK client it builds, so this applies uniformly to every transport (stdio, Streamable
/// HTTP, in-process).
/// </summary>
internal sealed class McpElicitationBridge(IHumanPromptRegistry humanPromptRegistry)
{

    /// <summary>
    /// The SDK client handlers for one connection. <paramref name="sink"/> is that connection's register
    /// of in-flight attended callers, the only route by which the handler can learn which turn a
    /// server-initiated request belongs to.
    /// </summary>
    internal McpClientHandlers CreateClientHandlers(McpElicitationSink sink)
    {

        ArgumentNullException.ThrowIfNull(sink);

        return new McpClientHandlers
        {
            ElicitationHandler = (request, cancellationToken) => HandleElicitationAsync(sink, request, cancellationToken),
        };

    }

    /// <summary>
    /// Bridges MCP <c>elicitation/create</c> to the same HITL channel as <c>ask_human</c>. The SDK runs
    /// this handler on the connection's receive loop, whose execution context was captured at connect
    /// time, so <see cref="HumanPromptLiveEmitterAmbient"/> is never visible here; the emitter comes from
    /// the connection's <see cref="McpElicitationSink"/>, which the tool call entered. A request with no
    /// attended caller, or with callers from more than one turn, is declined (never an invisible waiter).
    /// </summary>
    private async ValueTask<ModelContextProtocol.Protocol.ElicitResult> HandleElicitationAsync(
        McpElicitationSink sink,
        ModelContextProtocol.Protocol.ElicitRequestParams? request,
        CancellationToken cancellationToken)
    {
        if (!sink.TryResolve(out IHumanPromptLiveEmitter? emitter, out string? routingDeclineReason))
        {
            return DeclineElicitation(routingDeclineReason);
        }

        if (!TryResolveTextCompatibleContentKey(request, out string contentKey, out string? schemaDeclineReason))
        {
            return DeclineElicitation(schemaDeclineReason ?? "Unsupported elicitation schema.");
        }

        IHumanPromptReservation reservation = await humanPromptRegistry
            .CreateReservationAsync(cancellationToken)
            .ConfigureAwait(false);

        string callId = string.IsNullOrWhiteSpace(request?.ElicitationId)
            ? Guid.NewGuid().ToString("N")
            : request.ElicitationId!;
        string question = string.IsNullOrWhiteSpace(request?.Message)
            ? "The tool is requesting operator input."
            : request.Message.Trim();

        try
        {
            await EmitAskHumanCompatibleToolCallAsync(
                    emitter,
                    callId,
                    question,
                    reservation.PromptId,
                    cancellationToken)
                .ConfigureAwait(false);

            string value = await reservation
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            await EmitAskHumanCompatibleToolResultAsync(
                    emitter,
                    callId,
                    value,
                    isError: false,
                    cancellationToken)
                .ConfigureAwait(false);

            return new ModelContextProtocol.Protocol.ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [contentKey] = JsonSerializer.SerializeToElement(value, McpJsonSerializerContext.Default.String),
                },
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            await reservation.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ModelContextProtocol.Protocol.ElicitResult DeclineElicitation(string reason) =>
        new()
        {
            Action = "decline",
            Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["reason"] = JsonSerializer.SerializeToElement(reason, McpJsonSerializerContext.Default.String),
            },
        };

    /// <summary>
    /// Text-only UI can satisfy free-text form elicitation (no schema, or a single string field).
    /// URL mode and multi-field / non-string schemas are declined immediately.
    /// </summary>
    private static bool TryResolveTextCompatibleContentKey(
        ModelContextProtocol.Protocol.ElicitRequestParams? request,
        out string contentKey,
        out string? declineReason)
    {
        contentKey = "value";
        declineReason = null;

        if (request is null)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(request.Mode)
            && !string.Equals(request.Mode, "form", StringComparison.OrdinalIgnoreCase))
        {
            declineReason =
                $"Elicitation mode '{request.Mode}' is not supported by the text-only operator UI.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Url))
        {
            declineReason = "URL-mode elicitation is not supported by the text-only operator UI.";
            return false;
        }

        ModelContextProtocol.Protocol.ElicitRequestParams.RequestSchema? schema = request.RequestedSchema;
        if (schema?.Properties is null || schema.Properties.Count == 0)
        {
            return true;
        }

        if (schema.Properties.Count != 1)
        {
            declineReason =
                "Structured multi-field elicitation schemas are not supported by the text-only operator UI.";
            return false;
        }

        KeyValuePair<string, ModelContextProtocol.Protocol.ElicitRequestParams.PrimitiveSchemaDefinition> only =
            schema.Properties.First();

        if (only.Value is not ModelContextProtocol.Protocol.ElicitRequestParams.StringSchema)
        {
            declineReason =
                "Only free-text (string) elicitation fields are supported by the text-only operator UI.";
            return false;
        }

        contentKey = only.Key;
        return true;
    }

    private static async Task EmitAskHumanCompatibleToolCallAsync(
        IHumanPromptLiveEmitter emitter,
        string callId,
        string question,
        string promptId,
        CancellationToken cancellationToken)
    {
        AskHumanParams args = new() { Question = question, PromptId = promptId };
        string argsJson = JsonSerializer.Serialize(args, McpJsonSerializerContext.Default.AskHumanParams);

        await emitter
            .EmitAsync(
                new IntelligenceEvent(
                    IntelligenceEventType.ToolCall,
                    "ask_human",
                    $"ask_human: {argsJson}",
                    null,
                    new IntelligenceToolCallEvent(callId, "ask_human", argsJson, Index: 0)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task EmitAskHumanCompatibleToolResultAsync(
        IHumanPromptLiveEmitter emitter,
        string callId,
        string text,
        bool isError,
        CancellationToken cancellationToken)
    {
        await emitter
            .EmitAsync(
                new IntelligenceEvent(
                    isError ? IntelligenceEventType.ToolError : IntelligenceEventType.ToolResult,
                    "ask_human",
                    text,
                    null,
                    new IntelligenceToolCallEvent(callId, "ask_human", text, Index: 0)),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
