using System.Reflection;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Conclave;

public sealed class ApprenticeExecutionPolicyTests
{

    [Fact]
    public void ClassifyStepFailure_NoFailure_ReturnsNone()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: false,
            escalationRequested: false,
            toolDenied: false,
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
            toolDenied: true,
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
            toolDenied: false,
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
            toolDenied: false,
            pauseOrCancelRequested: false,
            isRetryableError: false);

        Assert.Equal(StepFailureKind.EscalationRequested, kind);

    }

    [Fact]
    public void ClassifyStepFailure_ToolDenied_ReturnsTerminal()
    {

        StepFailureKind kind = ApprenticeExecutionPolicy.ClassifyStepFailure(
            stepFailed: true,
            escalationRequested: false,
            toolDenied: true,
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
            toolDenied: false,
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
    public void SanitizeOperatorMessage_does_not_split_a_surrogate_pair_at_the_cap()
    {

        // The sanitized text is persisted to Apprentice.ErrorMessage, into the plan's step result, into
        // the escalation checkpoint, and written to the Chronicle SSE stream. A boundary that lands
        // between the halves of an astral character would leave a lone surrogate that every one of those
        // writers silently replaces with U+FFFD, so the stored error stops round-tripping.
        string message = new string('x', 511) + "\U0001F600";

        string sanitized = ApprenticeExecutionPolicy.SanitizeOperatorMessage(message);

        Assert.Equal(512, sanitized.Length);

        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);

        Assert.DoesNotContain(sanitized, char.IsSurrogate);

        Assert.Equal(sanitized, Utf8Truncation.NormalizeInvalidUtf16(sanitized));

    }

    [Fact]
    public void SanitizeOperatorMessage_keeps_a_whole_surrogate_pair_that_fits_under_the_cap()
    {

        string message = new string('x', 510) + "\U0001F600" + new string('y', 40);

        string sanitized = ApprenticeExecutionPolicy.SanitizeOperatorMessage(message);

        Assert.Equal(513, sanitized.Length);

        Assert.EndsWith("\U0001F600…", sanitized, StringComparison.Ordinal);

        Assert.Equal(sanitized, Utf8Truncation.NormalizeInvalidUtf16(sanitized));

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
    public void Apprentice_reweave_accepts_more_than_former_step_ceiling()
    {
        const int stepCount = 101;
        List<PlanStep> steps = Enumerable.Range(1, stepCount)
            .Select(static index => new PlanStep
            {
                Index = index,
                Description = $"Step {index}",
            })
            .ToList();

        Result<List<PlanStep>> result = ApprenticeExecutionPolicy.ValidateReweaveSteps(steps);

        Assert.True(result.IsSuccess);
        Assert.Equal(stepCount, result.Value.Count);

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

    [Fact]
    public void Master_Apprentice_and_A2A_runtime_have_no_hidden_autonomy_termination_policy()
    {
        string[] forbiddenTerms =
        [
            "MaxRunSteps",
            "MaxStepRetries",
            "RetryBackoff",
            "MaxReweaves",
            "MaxPlanSteps",
            "MaxReweaveStepCount",
            "IsRunStepBudgetExceeded",
            "IsReweaveBudgetExceeded",
            "InferenceTimeout",
            "StepTimeout",
            "MaxRunDuration",
            "ExternalTaskTimeout",
            "IsRunDurationBudgetExceeded",
            "TerminateRunBudget",
        ];
        Type[] runtimeTypes =
        [
            typeof(ApprenticeSettings),
            typeof(IntelligenceSettings),
            typeof(ArcanumSettingClamps),
            typeof(ApprenticeExecutionPolicy),
            typeof(ApprenticePlanParser),
            typeof(ApprenticeService),
            typeof(ConclaveA2ASettings),
            typeof(A2AClientService),
            typeof(WizardIntelligenceProvider),
        ];
        const BindingFlags flags =
            BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;
        List<string> violations = [];

        foreach (Type type in runtimeTypes)
        {
            violations.AddRange(type
                .GetMembers(flags)
                .Where(member => forbiddenTerms.Any(term =>
                    member.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Select(member => $"{type.Name}.{member.Name}"));
            violations.AddRange(type
                .GetMethods(flags)
                .SelectMany(method => method.GetParameters().Select(parameter =>
                    (Method: method, Parameter: parameter)))
                .Where(entry => entry.Parameter.Name is not null
                    && forbiddenTerms.Any(term => entry.Parameter.Name.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase)))
                .Select(entry =>
                    $"{type.Name}.{entry.Method.Name}({entry.Parameter.Name})"));
        }

        Assert.Empty(violations);

    }

}
