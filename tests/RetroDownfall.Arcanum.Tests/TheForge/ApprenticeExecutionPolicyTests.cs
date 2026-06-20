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
    public void ComputeBackoff_UsesExponentialCap(int attempt, int baseSeconds, int maxSeconds, int expectedSeconds)
    {

        TimeSpan backoff = ApprenticeExecutionPolicy.ComputeBackoff(attempt, baseSeconds, maxSeconds);

        Assert.Equal(expectedSeconds, (int)backoff.TotalSeconds);

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
    public void TryParseRevisedPlan_RejectsNoChange()
    {

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan("NO_CHANGE", out List<PlanStep>? steps);

        Assert.False(parsed);

        Assert.Null(steps);

    }

    [Fact]
    public void TryParseRevisedPlan_ParsesValidArray()
    {

        const string json = """[{"index":1,"description":"Revised step"}]""";

        bool parsed = ApprenticePlanParser.TryParseRevisedPlan(json, out List<PlanStep>? steps);

        Assert.True(parsed);

        Assert.NotNull(steps);

        Assert.Single(steps!);

        Assert.Equal("Revised step", steps![0].Description);

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

    }

}
