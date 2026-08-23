namespace RetroDownfall.Arcanum.Core.Conclave;

using RetroDownfall.Arcanum.Core.Primitives;

public enum StepFailureKind
{

    None,

    Retryable,

    Terminal,

    EscalationRequested,

    PausedOrCancelled,

}

public static class ApprenticeExecutionPolicy
{
    public const string PetitionDungeonMasterToolName = "petition_dungeon_master";

    public static StepFailureKind ClassifyStepFailure(
        bool stepFailed,
        bool escalationRequested,
        bool wardDenied,
        bool forbiddenArtDenied,
        bool pauseOrCancelRequested,
        bool isRetryableError)
    {

        if (pauseOrCancelRequested)
        {

            return StepFailureKind.PausedOrCancelled;

        }

        if (escalationRequested)
        {

            return StepFailureKind.EscalationRequested;

        }

        if (!stepFailed)
        {

            return StepFailureKind.None;

        }

        if (wardDenied || forbiddenArtDenied)
        {

            return StepFailureKind.Terminal;

        }

        if (isRetryableError)
        {

            return StepFailureKind.Retryable;

        }

        return StepFailureKind.Terminal;

    }

    public static bool IsReweavableStatus(string status) =>
        string.Equals(status, ApprenticeStatus.Paused.ToString(), StringComparison.Ordinal)
        || string.Equals(status, ApprenticeStatus.Escalated.ToString(), StringComparison.Ordinal);

    public static bool IsEscalatedStatus(string status) =>
        string.Equals(status, ApprenticeStatus.Escalated.ToString(), StringComparison.Ordinal);

    public static string SanitizeOperatorMessage(string? message, int maxLength = 512)
    {

        if (string.IsNullOrWhiteSpace(message))
        {

            return "An unexpected error occurred during step execution.";

        }

        string trimmed = message.Trim();

        if (trimmed.Length <= maxLength)
        {

            return trimmed;

        }

        // Nudged back one char when the cap would land between the halves of a surrogate pair: this text
        // is persisted to the apprentice row, the plan JSON and the escalation checkpoint, and a lone
        // surrogate becomes U+FFFD in every writer it passes through.
        return trimmed[..Utf8Truncation.SafeCharSliceLength(trimmed, maxLength)] + "…";

    }

    public static List<PlanStep> MergePlanTail(
        IReadOnlyList<PlanStep> currentPlan,
        int currentStepIndex,
        IReadOnlyList<PlanStep> revisedTail)
    {

        List<PlanStep> merged = new(currentStepIndex + revisedTail.Count);

        for (int i = 0; i < currentStepIndex && i < currentPlan.Count; i++)
        {

            merged.Add(currentPlan[i]);

        }

        for (int i = 0; i < revisedTail.Count; i++)
        {

            PlanStep step = revisedTail[i];

            merged.Add(step with
            {
                Index = step.Index > 0 ? step.Index : currentStepIndex + i + 1,
                Status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status,
            });

        }

        return merged;

    }

    public static Result<List<PlanStep>> ValidateReweaveSteps(IReadOnlyList<PlanStep>? steps)
    {

        if (steps is null || steps.Count == 0)
        {

            return Result<List<PlanStep>>.Failure(
                new Error(ErrorCodes.Apprentice.InvalidPlan, "At least one plan step is required."));

        }

        List<PlanStep> normalized = new(steps.Count);

        for (int i = 0; i < steps.Count; i++)
        {

            PlanStep step = steps[i];

            if (string.IsNullOrWhiteSpace(step.Description))
            {

                return Result<List<PlanStep>>.Failure(
                    new Error(ErrorCodes.Apprentice.InvalidPlan, "Every plan step must include a description."));

            }

            normalized.Add(step with
            {
                Index = step.Index > 0 ? step.Index : i + 1,
                Status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status,
            });

        }

        return Result<List<PlanStep>>.Success(normalized);

    }

}
