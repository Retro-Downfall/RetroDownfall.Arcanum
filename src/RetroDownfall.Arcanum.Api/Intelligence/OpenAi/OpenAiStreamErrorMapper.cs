using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

internal static class OpenAiStreamErrorMapper
{
    private const string GenericFailureMessage = "Inference failed. See server logs for details.";

    private static readonly HashSet<string> AllowedMessages =
    [
        GenericFailureMessage,
        "Tool invocation limit reached.",
        "Inference timed out.",
        "The requested model is not configured. Check Arcanum:Providers and Arcanum:DefaultModel.",
        "Prompt is required.",
        "Attached file validation failed.",
    ];

    public static OpenAiErrorDetail Map(Error? error)
    {
        string? internalCode = error?.Code;
        string? reasoningValidationCode = MapReasoningValidationCode(internalCode);

        if (reasoningValidationCode is not null)
        {
            return new OpenAiErrorDetail(
                "The reasoning options are invalid for the selected model.",
                "invalid_request_error",
                Param: "reasoning",
                Code: reasoningValidationCode);
        }

        return internalCode switch
        {
            ErrorCodes.Guardrails.Blocked or ErrorCodes.Guardrails.PiiDetected =>
                new OpenAiErrorDetail(
                    "The response was blocked by a configured guardrail policy.",
                    "api_error",
                    Param: null,
                    Code: "content_filter"),

            ErrorCodes.StructuredOutput.ValidationFailed =>
                new OpenAiErrorDetail(
                    "The streamed response failed structured-output validation.",
                    "api_error",
                    Param: null,
                    Code: "validation_failed"),

            ErrorCodes.StructuredOutput.SchemaInvalid =>
                new OpenAiErrorDetail(
                    "The configured structured-output schema is invalid.",
                    "api_error",
                    Param: null,
                    Code: "invalid_schema"),

            _ => new OpenAiErrorDetail(
                SanitizeMessage(error?.Message),
                "api_error",
                Param: null,
                Code: ResolveGenericCode(internalCode)),
        };
    }

    public static bool IsReasoningValidationCode(string? internalCode) =>
        MapReasoningValidationCode(internalCode) is not null;

    private static string SanitizeMessage(string? message) =>
        !string.IsNullOrWhiteSpace(message) && AllowedMessages.Contains(message)
            ? message
            : GenericFailureMessage;

    private static string ResolveGenericCode(string? internalCode) =>
        internalCode is ErrorCodes.Hub.ToolLoop or ErrorCodes.Hub.Timeout
            ? "server_error"
            : "inference_failed";

    private static string? MapReasoningValidationCode(string? internalCode) =>
        internalCode switch
        {
            ErrorCodes.Validation.InvalidReasoningEffort => "invalid_reasoning_effort",
            ErrorCodes.Validation.InvalidReasoningOutput => "invalid_reasoning_output",
            ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive =>
                "invalid_reasoning_options",
            ErrorCodes.Validation.InvalidReasoningBudget => "invalid_reasoning_budget",
            ErrorCodes.Validation.UnsupportedReasoningControl =>
                "unsupported_reasoning_control",
            ErrorCodes.Validation.ReasoningBudgetExceedsModelLimit =>
                "reasoning_budget_exceeds_model_limit",
            ErrorCodes.Validation.UnsupportedReasoningOutput =>
                "unsupported_reasoning_output",
            _ => null,
        };
}
