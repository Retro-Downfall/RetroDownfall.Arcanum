using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.TheForge;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api;

/// <summary>
/// Shared OpenAI chat-completion request validation used by live <c>POST /v1/chat/completions</c>
/// and <c>/v1/batches</c> JSONL lines so both paths reject the same shapes before inference.
/// </summary>
internal static partial class OpenAiV1Endpoints
{

    internal sealed record ChatCompletionValidationFailure(
        string Message,
        string Type,
        string? Code,
        string? Param,
        int StatusCode);

    /// <summary>
    /// Validates an OpenAI chat body the same way as the live HTTP handler. On success,
    /// <paramref name="ping"/> is the mapped <see cref="PingRequest"/> (optionally forced to
    /// zero tools for batches).
    /// </summary>
    internal static ChatCompletionValidationFailure? TryValidateChatCompletionRequest(
        OpenAiChatRequest body,
        ArcanumSettings settings,
        out PingRequest? ping,
        bool forceDisableAllTools = false)
    {
        ping = null;

        if (string.IsNullOrWhiteSpace(body.Model))
        {
            return new ChatCompletionValidationFailure(
                "`model` is required.",
                "invalid_request_error",
                "missing_required_parameter",
                "model",
                StatusCodes.Status400BadRequest);
        }

        if (body.Messages is null || body.Messages.Count == 0)
        {
            return new ChatCompletionValidationFailure(
                "`messages` is required and must be non-empty.",
                "invalid_request_error",
                "missing_required_parameter",
                "messages",
                StatusCodes.Status400BadRequest);
        }

        Result messageCountBounds = PingRequestBoundsValidator.ValidateOpenApiMessageCount(body.Messages.Count, settings);

        if (messageCountBounds.IsFailure)
        {
            return new ChatCompletionValidationFailure(
                messageCountBounds.Error.Message,
                "invalid_request_error",
                "invalid_value",
                "messages",
                StatusCodes.Status400BadRequest);
        }

        for (int i = 0; i < body.Messages.Count; i++)
        {
            OpenAiChatMessage m = body.Messages[i];

            if (string.IsNullOrWhiteSpace(m.Role))
            {
                return new ChatCompletionValidationFailure(
                    $"messages[{i}].role is required.",
                    "invalid_request_error",
                    "missing_required_parameter",
                    $"messages[{i}].role",
                    StatusCodes.Status400BadRequest);
            }

            if (!AllowedRoles.Contains(m.Role))
            {
                return new ChatCompletionValidationFailure(
                    $"messages[{i}].role '{m.Role}' is not one of {string.Join(", ", AllowedRoles)}.",
                    "invalid_request_error",
                    "invalid_value",
                    $"messages[{i}].role",
                    StatusCodes.Status400BadRequest);
            }

            if (m.Content?.Parts is { } parts)
            {
                int maxParts = ArcanumSettingClamps.MaxContentPartsPerMessage(
                    settings.ResolveIntelligence().MaxContentPartsPerMessage);

                if (parts.Length > maxParts)
                {
                    return new ChatCompletionValidationFailure(
                        $"messages[{i}].content has {parts.Length} parts; the maximum is {maxParts}.",
                        "invalid_request_error",
                        "invalid_value",
                        $"messages[{i}].content",
                        StatusCodes.Status400BadRequest);
                }

                for (int j = 0; j < parts.Length; j++)
                {
                    OpenAiContentPart part = parts[j];

                    if (part is not null && !IsSupportedContentPartType(part.Type))
                    {
                        return new ChatCompletionValidationFailure(
                            $"messages[{i}].content[{j}].type '{part.Type}' is not supported; expected 'text' or 'image_url'.",
                            "invalid_request_error",
                            "invalid_value",
                            $"messages[{i}].content[{j}].type",
                            StatusCodes.Status400BadRequest);
                    }
                }
            }
        }

        if (body.N is int n && n != 1)
        {
            return new ChatCompletionValidationFailure(
                "`n` must be 1 when specified. Arcanum does not support multiple completion choices.",
                "invalid_request_error",
                "invalid_value",
                "n",
                StatusCodes.Status400BadRequest);
        }

        ClientToolForwardingSettings clientTools = settings.ResolveClientTools();
        bool forwardClientTools = clientTools.Enabled && !forceDisableAllTools;

        if (body.Tools is { Length: > 0 })
        {
            if (!forwardClientTools)
            {
                return new ChatCompletionValidationFailure(
                    "Client-supplied `tools` are not supported. Arcanum uses its own server-side MCP toolset.",
                    "invalid_request_error",
                    "unsupported_parameter",
                    "tools",
                    StatusCodes.Status400BadRequest);
            }

            int schemaMaxDepth = ArcanumSettingClamps.JsonSchemaMaxDepth(
                ArcanumRuntimeDefaults.StructuredOutput.SchemaMaxDepth);

            Result toolsValidation = ValidateClientTools(
                body.Tools,
                clientTools.MaxClientTools,
                schemaMaxDepth);

            if (toolsValidation.IsFailure)
            {
                return new ChatCompletionValidationFailure(
                    toolsValidation.Error.Message,
                    "invalid_request_error",
                    MapClientToolsErrorCode(toolsValidation.Error.Code),
                    "tools",
                    ArcanumErrorMapper.ResolveStatusCode(toolsValidation.Error.Code));
            }
        }

        if (body.ToolChoice is { } toolChoice
            && toolChoice.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (!IsValidClientToolChoice(toolChoice))
            {
                return new ChatCompletionValidationFailure(
                    "Client-supplied `tool_choice` is not a valid shape; expected 'auto', 'none', 'required', or an object with type 'function' and function.name.",
                    "invalid_request_error",
                    "invalid_schema",
                    "tool_choice",
                    StatusCodes.Status400BadRequest);
            }

            if (!forwardClientTools)
            {
                if (toolChoice.ValueKind == JsonValueKind.Object)
                {
                    return new ChatCompletionValidationFailure(
                        "Client-supplied `tool_choice` named function is not supported. Arcanum uses its own server-side MCP toolset.",
                        "invalid_request_error",
                        "unsupported_parameter",
                        "tool_choice",
                        StatusCodes.Status400BadRequest);
                }
            }
            else if (toolChoice.ValueKind == JsonValueKind.Object
                && body.Tools is { Length: > 0 } tools)
            {
                string? functionName = toolChoice.TryGetProperty("function", out JsonElement functionElement)
                    && functionElement.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString()
                    : null;

                bool found = functionName is not null
                    && tools.Any(t => string.Equals(t.Function?.Name, functionName, StringComparison.Ordinal));

                if (!found)
                {
                    return new ChatCompletionValidationFailure(
                        $"Client-supplied `tool_choice` references unknown function '{functionName}'; it must match one of the supplied tools.",
                        "invalid_request_error",
                        "invalid_value",
                        "tool_choice",
                        StatusCodes.Status400BadRequest);
                }
            }
        }

        PingRequest mapped = OpenAiChatCompletionMapper.ToPingRequest(body, forwardClientTools);

        if (forceDisableAllTools)
        {
            mapped = mapped with
            {
                DisableAllTools = true,
                DisableMcpTools = true,
                ToolPolicy = ToolPolicy.NoTools,
                ForwardClientTools = false,
                ClientTools = null,
                ClientToolChoice = null,
            };
        }

        Result reasoningShape = ReasoningRequestValidator.Validate(mapped.Reasoning);

        if (reasoningShape.IsFailure)
        {
            return MapReasoningValidationFailure(reasoningShape.Error);
        }

        Result pingBounds = PingRequestBoundsValidator.Validate(mapped, settings);

        if (pingBounds.IsFailure)
        {
            return new ChatCompletionValidationFailure(
                pingBounds.Error.Message,
                "invalid_request_error",
                "invalid_value",
                null,
                StatusCodes.Status400BadRequest);
        }

        if (!ProviderResolver.TryResolveProviderForModel(settings, body.Model, out ProviderSettings? resolvedProvider, out string resolvedModel)
            || resolvedProvider is null
            || string.IsNullOrWhiteSpace(resolvedModel))
        {
            return new ChatCompletionValidationFailure(
                $"Model '{body.Model}' is not configured on any provider.",
                "invalid_request_error",
                "model_not_found",
                "model",
                StatusCodes.Status404NotFound);
        }

        _ = ProviderResolver.TryResolveModelEntry(
            resolvedProvider,
            resolvedModel,
            out ModelEntry? modelEntry);
        Result reasoningModel = ReasoningRequestValidator.ValidateForModel(
            mapped.Reasoning,
            modelEntry?.Reasoning,
            resolvedModel,
            resolvedProvider.Name);

        if (reasoningModel.IsFailure)
        {
            return MapReasoningValidationFailure(reasoningModel.Error);
        }

        if (ScryingValidator.RequestContainsImages(mapped))
        {
            ScryingSettings scrying = settings.ResolveScrying();

            if (!scrying.Enabled)
            {
                return new ChatCompletionValidationFailure(
                    "Scrying is disabled. Enable Arcanum:Features:Scrying to send images.",
                    "invalid_request_error",
                    "feature_disabled",
                    null,
                    StatusCodes.Status403Forbidden);
            }

            Result scryingShape = ScryingValidator.ValidateRequestImages(mapped, scrying);

            if (scryingShape.IsFailure)
            {
                return new ChatCompletionValidationFailure(
                    scryingShape.Error.Message,
                    "invalid_request_error",
                    MapScryingOpenAiErrorCode(scryingShape.Error.Code),
                    null,
                    ArcanumErrorMapper.ResolveStatusCode(scryingShape.Error.Code));
            }

            if (!ProviderResolver.SupportsVision(resolvedProvider, resolvedModel))
            {
                return new ChatCompletionValidationFailure(
                    $"Model '{resolvedModel}' does not support vision. Use a vision-capable model.",
                    "invalid_request_error",
                    "vision_not_supported",
                    "model",
                    StatusCodes.Status400BadRequest);
            }
        }

        ping = mapped;

        return null;
    }

    private static ChatCompletionValidationFailure MapReasoningValidationFailure(Error error)
    {
        OpenAiErrorDetail detail = OpenAiStreamErrorMapper.Map(error);

        return new ChatCompletionValidationFailure(
            detail.Message,
            detail.Type,
            detail.Code,
            detail.Param,
            ArcanumErrorMapper.ResolveStatusCode(error.Code));
    }

}
