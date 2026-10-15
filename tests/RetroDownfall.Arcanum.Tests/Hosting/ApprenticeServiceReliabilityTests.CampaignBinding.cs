using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// An Apprentice is as contained as the operator's own turn. Its Sanctum is the Sanctum of the Campaign it is bound to, so every turn it
/// runs (planning, a step, a parallel branch, the Shifting Fate evaluation) names that Campaign. Without it, an Apprentice bound to a
/// Campaign whose workspace sits outside the Campaign's root had no registered root to find its Campaign from, and ran unrestricted.
/// </summary>
public sealed partial class ApprenticeServiceReliabilityTests
{
    [Fact]
    public async Task Planning_a_step_and_evaluating_the_plan_each_name_the_campaign_the_apprentice_is_bound_to()
    {
        Guid apprenticeId = Guid.NewGuid();

        Guid campaignId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.CampaignId = campaignId;

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        CancellationSensitiveRepository repo = new(apprentice);

        RequestCapturingIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);

        Assert.Contains("plan", intelligence.Kinds);

        Assert.Contains("step", intelligence.Kinds);

        Assert.Contains("evaluation", intelligence.Kinds);

        Assert.All(intelligence.Requests, request => Assert.Equal(campaignId, request.CampaignId));
    }

    [Fact]
    public async Task Every_branch_of_a_parallel_plan_names_the_campaign_the_apprentice_is_bound_to()
    {
        Guid apprenticeId = Guid.NewGuid();

        Guid campaignId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.CampaignId = campaignId;

        apprentice.Plan = ApprenticeRepository.SerializePlan(
        [
            new PlanStep { Index = 0, Description = "First branch", IsParallel = true },
            new PlanStep { Index = 1, Description = "Second branch", IsParallel = true },
        ]);

        CancellationSensitiveRepository repo = new(apprentice);

        RequestCapturingIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(2, intelligence.Kinds.Count(static kind => kind == "branch"));

        Assert.All(intelligence.Requests, request => Assert.Equal(campaignId, request.CampaignId));
    }

    // The negative twin: an Apprentice bound to no Campaign names none, so nothing changes for it.
    [Fact]
    public async Task An_apprentice_bound_to_no_campaign_names_none()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = RunningApprenticeWithOneStep(apprenticeId);

        apprentice.Status = ApprenticeStatus.Idle.ToString();

        apprentice.Plan = "[]";

        CancellationSensitiveRepository repo = new(apprentice);

        RequestCapturingIntelligence intelligence = new();

        using ApprenticeService service = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        Result<string> started = await service.StartAsync(apprenticeId, CancellationToken.None);

        Assert.True(started.IsSuccess, started.IsFailure ? started.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.NotEmpty(intelligence.Requests);

        Assert.All(intelligence.Requests, static request => Assert.Null(request.CampaignId));
    }

    /// <summary>Plans one step, completes every step, changes nothing on evaluation, and keeps every request it was asked to run.</summary>
    private sealed class RequestCapturingIntelligence : IArcanumIntelligenceProvider
    {
        private readonly ConcurrentQueue<(string Kind, PingRequest Request)> _seen = new();

        internal IReadOnlyList<string> Kinds => [.. _seen.Select(static entry => entry.Kind)];

        internal IReadOnlyList<PingRequest> Requests => [.. _seen.Select(static entry => entry.Request)];

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            if (request.Prompt.Contains("creating a plan", StringComparison.Ordinal))
            {
                _seen.Enqueue(("plan", request));

                return Task.FromResult<Result<PromptTurnResult>>(
                    new PromptTurnResult(
                        """[{"index":1,"description":"One durable step"}]""",
                        Usage: null));
            }

            _seen.Enqueue(("evaluation", request));

            return Task.FromResult<Result<PromptTurnResult>>(new PromptTurnResult("NO_CHANGE", Usage: null));
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            _seen.Enqueue((request.StatelessMessages is { Count: > 0 } ? "branch" : "step", request));

            await Task.Yield();

            yield return new IntelligenceEvent(IntelligenceEventType.Result, "step complete");
        }
    }
}
