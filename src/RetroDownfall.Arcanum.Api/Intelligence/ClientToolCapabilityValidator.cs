using System.Text.Json;
using Microsoft.Extensions.AI;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Validates client tool-choice shape, the policy-filtered tool set, and the resolved model's
/// declared capability. Optional tool use may safely degrade to no tools, but a required or named
/// choice must remain satisfiable throughout inference preparation and provider fallback.
/// </summary>
internal static class ClientToolCapabilityValidator
{
    internal enum ChoiceMode
    {
        Unspecified,
        Auto,
        None,
        Required,
        Specific,
    }

    internal readonly record struct ParsedChoice(
        ChoiceMode Mode,
        string? FunctionName = null)
    {
        internal bool RequiresToolCall =>
            Mode is ChoiceMode.Required or ChoiceMode.Specific;
    }

    internal static Result ValidateRequest(
        PingRequest request,
        ArcanumSettings settings)
    {
        if (!request.ForwardClientTools)
        {
            return Result.Success();
        }

        ClientToolForwardingSettings clientTools = settings.ResolveClientTools();

        if (!clientTools.Enabled)
        {
            return Result.Failure(new Error(
                ErrorCodes.ClientTools.Disabled,
                "Client tool forwarding is disabled. Enable Arcanum:Features:ClientTools to use client-supplied tools."));
        }

        if (request.ClientTools is { } tools)
        {
            Result definitions = ValidateToolDefinitions(
                tools,
                clientTools.MaxClientTools,
                ArcanumSettingClamps.JsonSchemaMaxDepth(
                    ArcanumRuntimeDefaults.StructuredOutput.SchemaMaxDepth));

            if (definitions.IsFailure)
            {
                return definitions;
            }
        }

        if (!TryParse(request.ClientToolChoice, out ParsedChoice choice))
        {
            return Result.Failure(
                new Error(
                    ErrorCodes.ClientTools.InvalidSchema,
                    "Client tool choice must be 'auto', 'none', 'required', or a named function."));
        }

        if (choice.RequiresToolCall
            && request.ClientTools is not { Length: > 0 })
        {
            return Result.Failure(CreateToolChoiceUnavailableError(choice));
        }

        if (choice.FunctionName is { } functionName
            && !request.ClientTools!.Any(tool => string.Equals(
                tool.Function?.Name,
                functionName,
                StringComparison.Ordinal)))
        {
            return Result.Failure(CreateToolChoiceUnavailableError(choice));
        }

        return Result.Success();
    }

    internal static Result ValidateForModel(
        PingRequest request,
        ModelEntry? modelEntry,
        string resolvedModel,
        string providerName)
    {
        if (!request.ForwardClientTools
            || modelEntry is not { SupportsTools: false }
            || !RequiresToolCall(request))
        {
            return Result.Success();
        }

        return Result.Failure(CreateModelUnsupportedError(resolvedModel, providerName));
    }

    internal static Result ValidateToolDefinitions(
        OpenAiToolDefinition[] tools,
        int configuredMaxClientTools,
        int schemaMaxDepth)
    {
        int maxClientTools = ArcanumSettingClamps.ClientToolForwardingMaxClientTools(
            configuredMaxClientTools);

        if (tools.Length > maxClientTools)
        {
            return Result.Failure(new Error(
                ErrorCodes.ClientTools.TooMany,
                $"Client-supplied tools exceed the maximum of {maxClientTools}."));
        }

        HashSet<string> seenNames = new(StringComparer.Ordinal);

        for (int index = 0; index < tools.Length; index++)
        {
            OpenAiToolDefinition? tool = tools[index];

            if (tool is null)
            {
                return InvalidDefinition($"tools[{index}] cannot be null.");
            }

            if (!string.Equals(tool.Type, "function", StringComparison.Ordinal))
            {
                return InvalidDefinition($"tools[{index}].type must be 'function'.");
            }

            if (string.IsNullOrWhiteSpace(tool.Function?.Name))
            {
                return InvalidDefinition($"tools[{index}].function.name is required.");
            }

            if (!seenNames.Add(tool.Function.Name))
            {
                return InvalidDefinition(
                    $"Duplicate tool function name '{tool.Function.Name}'.");
            }

            JsonElement? parameters = tool.Function.Parameters;

            if (parameters is not { } parametersElement
                || parametersElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (parametersElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidDefinition(
                    $"tools[{index}].function.parameters must be a valid JSON Schema object.");
            }

            using JsonDocument parametersDocument = JsonDocument.Parse(
                parametersElement.GetRawText());

            Result<JsonSchemaDefinition> parsedSchema = JsonSchemaHelper.Parse(
                parametersDocument,
                schemaMaxDepth);

            if (parsedSchema.IsFailure)
            {
                return InvalidDefinition(
                    $"tools[{index}].function.parameters is not a valid JSON Schema: "
                    + parsedSchema.Error.Message);
            }
        }

        return Result.Success();
    }

    internal static Result ValidateEffectiveToolSet(
        PingRequest request,
        IReadOnlyList<AITool> effectiveTools)
    {
        if (!request.ForwardClientTools)
        {
            return Result.Success();
        }

        if (!TryParse(request.ClientToolChoice, out ParsedChoice choice))
        {
            return Result.Failure(
                new Error(
                    ErrorCodes.ClientTools.InvalidSchema,
                    "Client tool choice must be 'auto', 'none', 'required', or a named function."));
        }

        bool available = choice.Mode switch
        {
            ChoiceMode.Required => effectiveTools.Count > 0,
            ChoiceMode.Specific => effectiveTools
                .OfType<AIFunction>()
                .Any(tool => string.Equals(
                    tool.Name,
                    choice.FunctionName,
                    StringComparison.Ordinal)),
            _ => true,
        };

        return available
            ? Result.Success()
            : Result.Failure(CreateToolChoiceUnavailableError(choice));
    }

    internal static bool RequiresToolCall(PingRequest request) =>
        request.ForwardClientTools
        && TryParse(request.ClientToolChoice, out ParsedChoice choice)
        && choice.RequiresToolCall;

    internal static Error CreateModelUnsupportedError(
        string resolvedModel,
        string providerName) =>
        new(
            ErrorCodes.ClientTools.ModelUnsupported,
            $"Model '{resolvedModel}' on provider '{providerName}' does not support required tool calls. "
            + "Use tool_choice 'auto' or 'none', or choose a model that supports tools.");

    internal static bool TryParse(
        JsonElement? toolChoice,
        out ParsedChoice parsed)
    {
        if (toolChoice is not { } choice
            || choice.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            parsed = new ParsedChoice(ChoiceMode.Unspecified);

            return true;
        }

        if (choice.ValueKind == JsonValueKind.String)
        {
            parsed = choice.GetString()?.Trim().ToLowerInvariant() switch
            {
                "auto" => new ParsedChoice(ChoiceMode.Auto),
                "none" => new ParsedChoice(ChoiceMode.None),
                "required" => new ParsedChoice(ChoiceMode.Required),
                _ => default,
            };

            return parsed.Mode is not ChoiceMode.Unspecified;
        }

        if (choice.ValueKind != JsonValueKind.Object
            || !choice.TryGetProperty("type", out JsonElement typeElement)
            || typeElement.ValueKind != JsonValueKind.String
            || !string.Equals(typeElement.GetString(), "function", StringComparison.Ordinal)
            || !choice.TryGetProperty("function", out JsonElement functionElement)
            || functionElement.ValueKind != JsonValueKind.Object
            || !functionElement.TryGetProperty("name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            parsed = default;

            return false;
        }

        parsed = new ParsedChoice(ChoiceMode.Specific, nameElement.GetString());

        return true;
    }

    private static Error CreateToolChoiceUnavailableError(ParsedChoice choice) =>
        new(
            ErrorCodes.ClientTools.ToolChoiceUnavailable,
            choice.FunctionName is { } functionName
                ? $"Required client tool '{functionName}' is unavailable after Arcanum policy filtering."
                : "Required client tool choice cannot be satisfied because no client tools are available.");

    private static Result InvalidDefinition(string message) =>
        Result.Failure(new Error(ErrorCodes.ClientTools.InvalidSchema, message));
}
