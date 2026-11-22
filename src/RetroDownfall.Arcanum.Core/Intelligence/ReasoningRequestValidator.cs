using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Validates normalized reasoning request controls before provider I/O. Call
/// <see cref="ValidateForModel"/> for every resolved provider/model candidate so an explicit
/// unsupported control is never silently dropped during fallback.
/// </summary>
public static class ReasoningRequestValidator
{
    public static Result Validate(ReasoningRequestOptions? options)
    {
        if (options is null)
        {
            return Result.Success();
        }

        if (options.Effort is { } effort && !Enum.IsDefined(effort))
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.InvalidReasoningEffort,
                $"Reasoning effort '{effort}' is not supported."));
        }

        if (options.Output is { } output && !Enum.IsDefined(output))
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.InvalidReasoningOutput,
                $"Reasoning output mode '{output}' is not supported."));
        }

        if (options.Effort is not null && options.BudgetTokens is not null)
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.ReasoningEffortAndBudgetMutuallyExclusive,
                "Reasoning effort and BudgetTokens are mutually exclusive; specify only one."));
        }

        if (options.BudgetTokens is <= 0)
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.InvalidReasoningBudget,
                "Reasoning BudgetTokens must be a positive integer."));
        }

        return Result.Success();
    }

    public static Result ValidateForModel(
        ReasoningRequestOptions? options,
        ModelEntry? modelEntry,
        bool featuresReasoningEnabled,
        string? modelName = null,
        string? providerName = null)
    {
        Result intrinsic = Validate(options);

        if (intrinsic.IsFailure || options is null)
        {
            return intrinsic;
        }

        if (!featuresReasoningEnabled)
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.UnsupportedReasoningControl,
                "Reasoning is disabled by Features.Reasoning."));
        }

        string model = DescribeCandidate(providerName, modelName);

        bool reasoningCapable = modelEntry?.Reasoning?.WireDialect is not null;

        if (options.Effort is not null && !reasoningCapable)
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.UnsupportedReasoningControl,
                $"{model} does not declare reasoning capability."));
        }

        if (options.BudgetTokens is not null)
        {
            bool budgetCapable = modelEntry?.Reasoning?.WireDialect is ReasoningWireDialect.OpenRouter
                or ReasoningWireDialect.TopLevelReasoningBudget
                or ReasoningWireDialect.AnthropicThinking;

            if (!budgetCapable)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Validation.UnsupportedReasoningControl,
                    $"{model} does not support numeric reasoning budgets."));
            }
        }

        if (options.Output is ReasoningOutputMode.Summary or ReasoningOutputMode.Full)
        {
            // Best-effort: the provider returns what it returns; Arcanum projects only when
            // features.reasoningSummaries is enabled (checked separately at projection time).
            if (!reasoningCapable)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Validation.UnsupportedReasoningOutput,
                    $"{model} does not declare reasoning capability."));
            }
        }

        return Result.Success();
    }

    private static string DescribeCandidate(string? providerName, string? modelName)
    {
        if (!string.IsNullOrWhiteSpace(providerName) && !string.IsNullOrWhiteSpace(modelName))
        {
            return $"Provider '{providerName}' model '{modelName}'";
        }

        return string.IsNullOrWhiteSpace(modelName) ? "The selected model" : $"Model '{modelName}'";
    }
}
