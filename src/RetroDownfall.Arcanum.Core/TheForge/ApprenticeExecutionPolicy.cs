namespace RetroDownfall.Arcanum.Core.TheForge;

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

    public const int MaxReweaveStepCount = 100;

    public const string PetitionDungeonMasterToolName = "petition_dungeon_master";

    public static TimeSpan ComputeBackoff(int attemptNumber, int baseSeconds, int maxSeconds) =>
        ComputeBackoff(attemptNumber, baseSeconds, maxSeconds, Random.Shared);

    public static TimeSpan ComputeBackoff(int attemptNumber, int baseSeconds, int maxSeconds, Random jitterSource)
    {

        ArgumentNullException.ThrowIfNull(jitterSource);

        int ceilingSeconds = ComputeBackoffCeilingSeconds(attemptNumber, baseSeconds, maxSeconds);

        return ApplyFullJitter(ceilingSeconds, jitterSource);

    }

    internal static int ComputeBackoffCeilingSeconds(int attemptNumber, int baseSeconds, int maxSeconds)
    {

        int clampedBase = Math.Max(1, baseSeconds);

        int clampedMax = Math.Max(clampedBase, maxSeconds);

        int exponent = Math.Max(0, attemptNumber - 1);

        double scaled = clampedBase * Math.Pow(2, exponent);

        int seconds = (int)Math.Min(scaled, clampedMax);

        return Math.Max(1, seconds);

    }

    private static TimeSpan ApplyFullJitter(int ceilingSeconds, Random jitterSource)
    {

        if (ceilingSeconds <= 1)
        {

            return TimeSpan.FromSeconds(1);

        }

        int jitteredSeconds = jitterSource.Next(1, ceilingSeconds + 1);

        return TimeSpan.FromSeconds(jitteredSeconds);

    }

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

    public static bool IsRunStepBudgetExceeded(int stepsExecutedThisRun, int maxRunSteps) =>
        stepsExecutedThisRun >= maxRunSteps;

    public static bool IsRunDurationBudgetExceeded(TimeSpan elapsed, int maxRunDurationMinutes) =>
        elapsed >= TimeSpan.FromMinutes(Math.Max(1, maxRunDurationMinutes));

    public static bool IsReweaveBudgetExceeded(int reweavesThisRun, int maxReweavesPerRun) =>
        maxReweavesPerRun >= 0 && reweavesThisRun >= maxReweavesPerRun;

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

        return trimmed[..maxLength] + "…";

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

        if (steps.Count > MaxReweaveStepCount)
        {

            return Result<List<PlanStep>>.Failure(
                new Error(ErrorCodes.Apprentice.InvalidPlan, $"Plan may not exceed {MaxReweaveStepCount} steps."));

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
