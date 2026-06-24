using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.TheForge;

public sealed class ApprenticeExecutionPolicyTests
{

    [Theory]
    [InlineData(1, 5, 60, 5)]
    [InlineData(2, 5, 60, 10)]
    [InlineData(3, 5, 60, 20)]
    [InlineData(4, 5, 60, 40)]
    [InlineData(5, 5, 60, 60)]
    public void ComputeBackoffCeilingSeconds_UsesExponentialCap(int attempt, int baseSeconds, int maxSeconds, int expectedSeconds)
    {

        int ceilingSeconds = ApprenticeExecutionPolicy.ComputeBackoffCeilingSeconds(attempt, baseSeconds, maxSeconds);

        Assert.Equal(expectedSeconds, ceilingSeconds);

    }

    [Theory]
    [InlineData(0, 5, 60, 5)]
    [InlineData(-3, 5, 60, 5)]
    public void ComputeBackoffCeilingSeconds_NonPositiveAttempt_TreatsAsFirstAttempt(
        int attempt,
        int baseSeconds,
        int maxSeconds,
        int expectedSeconds)
    {

        int ceilingSeconds = ApprenticeExecutionPolicy.ComputeBackoffCeilingSeconds(attempt, baseSeconds, maxSeconds);

        Assert.Equal(expectedSeconds, ceilingSeconds);

    }

    [Fact]
    public void ComputeBackoff_WithDeterministicJitter_UsesFullJitterRange()
    {

        Random jitterSource = new(42);

        TimeSpan backoff = ApprenticeExecutionPolicy.ComputeBackoff(2, 5, 60, jitterSource);

        Assert.InRange((int)backoff.TotalSeconds, 1, 10);

    }

    [Fact]
    public void ComputeBackoff_WithDeterministicJitter_RepeatedCallsStayWithinCeiling()
    {

        Random jitterSource = new(99);

        for (int attempt = 1; attempt <= 5; attempt++)
        {

            int ceilingSeconds = ApprenticeExecutionPolicy.ComputeBackoffCeilingSeconds(attempt, 5, 60);

            for (int i = 0; i < 20; i++)
            {

                TimeSpan backoff = ApprenticeExecutionPolicy.ComputeBackoff(attempt, 5, 60, jitterSource);

                Assert.InRange((int)backoff.TotalSeconds, 1, ceilingSeconds);

            }

        }

    }

    [Fact]
    public void ClassifyStepFailure_NoFailure_ReturnsNone()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: false,
            escalationRequested: false,
            wardDenied: false,
            forbiddenArtDenied: false,
            pauseOrCancelRequested: false,
            isRetryableError: false);

        Assert.Equal(StepFailureKind.None, kind);

    }

    [Fact]
    public void ClassifyStepFailure_PauseRequested_TakesPrecedence()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: true,
            wardDenied: true,
            forbiddenArtDenied: true,
            pauseOrCancelRequested: true,
            isRetryableError: true);

        Assert.Equal(StepFailureKind.PausedOrCancelled, kind);

    }

    [Fact]
    public void ClassifyStepFailure_ReturnsRetryableForTransientErrors()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: false,
            wardDenied: false,
            forbiddenArtDenied: false,
            pauseOrCancelRequested: false,
            isRetryableError: true);

        Assert.Equal(StepFailureKind.Retryable, kind);

    }

    [Fact]
    public void ClassifyStepFailure_ReturnsEscalationWhenPetitioned()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: true,
            wardDenied: false,
            forbiddenArtDenied: false,
            pauseOrCancelRequested: false,
            isRetryableError: false);

        Assert.Equal(StepFailureKind.EscalationRequested, kind);

    }

    [Fact]
    public void ClassifyStepFailure_WardDenied_ReturnsTerminal()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: false,
            wardDenied: true,
            forbiddenArtDenied: false,
            pauseOrCancelRequested: false,
            isRetryableError: true);

        Assert.Equal(StepFailureKind.Terminal, kind);

    }

    [Fact]
    public void ClassifyStepFailure_ForbiddenArtDenied_ReturnsTerminal()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: false,
            wardDenied: false,
            forbiddenArtDenied: true,
            pauseOrCancelRequested: false,
            isRetryableError: true);

        Assert.Equal(StepFailureKind.Terminal, kind);

    }

    [Fact]
    public void ClassifyStepFailure_NonRetryableFailure_ReturnsTerminal()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: false,
            wardDenied: false,
            forbiddenArtDenied: false,
            pauseOrCancelRequested: false,
            isRetryableError: false);

        Assert.Equal(StepFailureKind.Terminal, kind);

    }

    [Theory]
    [InlineData("Paused", true)]
    [InlineData("Escalated", true)]
    [InlineData("Running", false)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    public void IsReweavableStatus_MatchesExpectedStatuses(string status, bool expected)
    {

        Assert.Equal(expected, ApprenticeExecutionPolicy.IsReweavableStatus(status));

    }

    [Theory]
    [InlineData("Escalated", true)]
    [InlineData("Running", false)]
    [InlineData("Paused", false)]
    public void IsEscalatedStatus_MatchesExpectedStatuses(string status, bool expected)
    {

        Assert.Equal(expected, ApprenticeExecutionPolicy.IsEscalatedStatus(status));

    }

    [Fact]
    public void SanitizeOperatorMessage_NullOrWhitespace_ReturnsDefault()
    {

        string message = ApprenticeExecutionPolicy.SanitizeOperatorMessage(null);

        Assert.Equal("An unexpected error occurred during step execution.", message);

        Assert.Equal(
            "An unexpected error occurred during step execution.",
            ApprenticeExecutionPolicy.SanitizeOperatorMessage("   "));

    }

    [Fact]
    public void SanitizeOperatorMessage_TruncatesLongMessages()
    {

        string longMessage = new string('x', 600);

        string sanitized = ApprenticeExecutionPolicy.SanitizeOperatorMessage(longMessage, maxLength: 10);

        Assert.Equal(11, sanitized.Length);

        Assert.EndsWith("…", sanitized);

        Assert.StartsWith("xxxxxxxxxx", sanitized);

    }

    [Fact]
    public void ValidateReweaveSteps_RejectsNullOrEmpty()
    {

        Result<List<PlanStep>> nullResult = ApprenticeExecutionPolicy.ValidateReweaveSteps(null);

        Assert.True(nullResult.IsFailure);

        Result<List<PlanStep>> emptyResult = ApprenticeExecutionPolicy.ValidateReweaveSteps([]);

        Assert.True(emptyResult.IsFailure);

        Assert.Equal("Apprentice.InvalidPlan", nullResult.Error.Code);

    }

    [Fact]
    public void ValidateReweaveSteps_RejectsTooManySteps()
    {

        List<PlanStep> steps = new(ApprenticeExecutionPolicy.MaxReweaveStepCount + 1);

        for (int i = 0; i <= ApprenticeExecutionPolicy.MaxReweaveStepCount; i++)
        {

            steps.Add(new PlanStep { Index = i + 1, Description = $"Step {i + 1}" });

        }

        Result<List<PlanStep>> result = ApprenticeExecutionPolicy.ValidateReweaveSteps(steps);

        Assert.True(result.IsFailure);

        Assert.Contains("100", result.Error.Message);

    }

    [Fact]
    public void ValidateReweaveSteps_RejectsEmptyDescriptions()
    {

        Result<List<PlanStep>> result = ApprenticeExecutionPolicy.ValidateReweaveSteps(
        [
            new PlanStep { Index = 1, Description = "   " },
        ]);

        Assert.True(result.IsFailure);

        Assert.Equal("Apprentice.InvalidPlan", result.Error.Code);

    }

    [Fact]
    public void ValidateReweaveSteps_NormalizesIndexAndStatus()
    {

        Result<List<PlanStep>> result = ApprenticeExecutionPolicy.ValidateReweaveSteps(
        [
            new PlanStep { Description = "First" },
            new PlanStep { Index = 5, Description = "Second", Status = "running" },
        ]);

        Assert.True(result.IsSuccess);

        Assert.Equal(1, result.Value[0].Index);

        Assert.Equal("pending", result.Value[0].Status);

        Assert.Equal(5, result.Value[1].Index);

        Assert.Equal("running", result.Value[1].Status);

    }

    [Fact]
    public void MergePlanTail_PreservesCompletedPrefix()
    {

        List<PlanStep> plan =
        [
            new() { Index = 1, Description = "Done", Status = "completed" },
            new() { Index = 2, Description = "Old pending" },
        ];

        List<PlanStep> tail =
        [
            new() { Index = 2, Description = "New pending" },
        ];

        List<PlanStep> merged = ApprenticeExecutionPolicy.MergePlanTail(plan, 1, tail);

        Assert.Equal(2, merged.Count);

        Assert.Equal("Done", merged[0].Description);

        Assert.Equal("New pending", merged[1].Description);

        Assert.Equal(2, merged[1].Index);

        Assert.Equal("pending", merged[1].Status);

    }

    [Fact]
    public void MergePlanTail_NormalizesTailIndexesAndStatuses()
    {

        List<PlanStep> plan =
        [
            new() { Index = 1, Description = "Done", Status = "completed" },
        ];

        List<PlanStep> tail =
        [
            new() { Description = "Tail one" },
            new() { Index = 0, Description = "Tail two", Status = "" },
        ];

        List<PlanStep> merged = ApprenticeExecutionPolicy.MergePlanTail(plan, 1, tail);

        Assert.Equal(3, merged.Count);

        Assert.Equal(2, merged[1].Index);

        Assert.Equal("pending", merged[1].Status);

        Assert.Equal(3, merged[2].Index);

        Assert.Equal("pending", merged[2].Status);

    }

    [Theory]
    [InlineData(99, 100, false)]
    [InlineData(100, 100, true)]
    [InlineData(101, 100, true)]
    public void IsRunStepBudgetExceeded_MatchesThreshold(int executed, int max, bool expected)
    {

        Assert.Equal(expected, ApprenticeExecutionPolicy.IsRunStepBudgetExceeded(executed, max));

    }

    [Fact]
    public void IsRunDurationBudgetExceeded_UsesMinuteThreshold()
    {

        Assert.False(ApprenticeExecutionPolicy.IsRunDurationBudgetExceeded(TimeSpan.FromMinutes(4), 5));

        Assert.True(ApprenticeExecutionPolicy.IsRunDurationBudgetExceeded(TimeSpan.FromMinutes(5), 5));

    }

    [Theory]
    [InlineData(9, 10, false)]
    [InlineData(10, 10, true)]
    public void IsReweaveBudgetExceeded_MatchesThreshold(int reweaves, int max, bool expected)
    {

        Assert.Equal(expected, ApprenticeExecutionPolicy.IsReweaveBudgetExceeded(reweaves, max));

    }

}
