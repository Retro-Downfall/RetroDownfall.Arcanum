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

        if (options.BudgetTokens is { } budgetTokens
            && budgetTokens != ArcanumSettingClamps.ReasoningBudgetTokens(budgetTokens))
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.InvalidReasoningBudget,
                $"Reasoning BudgetTokens ({budgetTokens}) must be within the 1-2,097,152 token range."));
        }

        return Result.Success();
    }

    public static Result ValidateForModel(
        ReasoningRequestOptions? options,
        ReasoningCapabilities? capabilities,
        string? modelName = null,
        string? providerName = null)
    {
        Result intrinsic = Validate(options);

        if (intrinsic.IsFailure || options is null)
        {
            return intrinsic;
        }

        string model = DescribeCandidate(providerName, modelName);

        if (options.Effort is not null && !SupportsEffort(capabilities))
        {
            return Result.Failure(new Error(
                ErrorCodes.Validation.UnsupportedReasoningControl,
                $"{model} does not declare support for the explicit reasoning effort control."));
        }

        if (options.BudgetTokens is { } budgetTokens)
        {
            if (!SupportsBudget(capabilities))
            {
                return Result.Failure(new Error(
                    ErrorCodes.Validation.UnsupportedReasoningControl,
                    $"{model} does not declare support for the explicit reasoning budget control."));
            }

            if (capabilities!.MaxBudgetTokens is { } maxBudgetTokens && budgetTokens > maxBudgetTokens)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Validation.ReasoningBudgetExceedsModelLimit,
                    $"{model} reasoning BudgetTokens ({budgetTokens}) exceeds its configured maximum ({maxBudgetTokens})."));
            }
        }

        if (options.Output is ReasoningOutputMode.Summary or ReasoningOutputMode.Full)
        {
            bool supportsRequestedOutput = capabilities is not null
                && capabilities.AllowsClientOutput
                && (options.Output == ReasoningOutputMode.Summary
                    ? capabilities.SupportsSummary
                    : capabilities.SupportsFull);

            if (!supportsRequestedOutput)
            {
                return Result.Failure(new Error(
                    ErrorCodes.Validation.UnsupportedReasoningOutput,
                    $"{model} does not permit the requested client reasoning output mode '{options.Output}'."));
            }
        }

        return Result.Success();
    }

    private static bool SupportsEffort(ReasoningCapabilities? capabilities) =>
        capabilities?.ControlSupport is ReasoningControlSupport.Effort
            or ReasoningControlSupport.EffortAndBudget;

    private static bool SupportsBudget(ReasoningCapabilities? capabilities) =>
        capabilities?.ControlSupport is ReasoningControlSupport.Budget
            or ReasoningControlSupport.EffortAndBudget;

    private static string DescribeCandidate(string? providerName, string? modelName)
    {
        if (!string.IsNullOrWhiteSpace(providerName) && !string.IsNullOrWhiteSpace(modelName))
        {
            return $"Provider '{providerName}' model '{modelName}'";
        }

        return string.IsNullOrWhiteSpace(modelName) ? "The selected model" : $"Model '{modelName}'";
    }
}
