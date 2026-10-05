using System.Reflection;
using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// Durability of an Apprentice step once its model and tool effects have run: the completion is the
/// point of no return, so Pause, Cancel, and host shutdown landing after it must neither lose it nor
/// make Resume pay for the same step again.
/// </summary>
public sealed partial class ApprenticeServiceReliabilityTests
{
    [Fact]
    public async Task Pause_during_shifting_fate_does_not_rerun_the_completed_step()
    {
        Guid apprenticeId = Guid.NewGuid();

        CancellationSensitiveRepository repo = new(RunningApprenticeWithOneStep(apprenticeId));

        ApprenticeService? service = null;

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: null,
            duringShiftingFate: () => CancelExecutionLease(service!, apprenticeId));

        using ApprenticeService created = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        service = created;

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.ShiftingFateCalls);

        AssertStepCommittedThenPaused(repo.Get(apprenticeId));

        await AssertResumeDoesNotRerunTheStepAsync(service, repo, intelligence, apprenticeId);
    }

    [Fact]
    public async Task Pause_between_step_stream_end_and_completion_commit_still_records_the_step_completed()
    {
        Guid apprenticeId = Guid.NewGuid();

        CancellationSensitiveRepository repo = new(RunningApprenticeWithOneStep(apprenticeId));

        ApprenticeService? service = null;

        StepThenShiftingFateIntelligence intelligence = new(
            afterStreamResult: () => CancelExecutionLease(service!, apprenticeId),
            duringShiftingFate: null);

        using ApprenticeService created = CreateService(
            repo,
            CreateCapacitySettings(),
            new CapturingLogger<ApprenticeService>(),
            intelligence);

        service = created;

        Assert.True(TryAcquireExecutionSlot(service, apprenticeId));

        BeginExecutionTask(service, apprenticeId);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        AssertStepCommittedThenPaused(repo.Get(apprenticeId));

        await AssertResumeDoesNotRerunTheStepAsync(service, repo, intelligence, apprenticeId);
    }

    private static void AssertStepCommittedThenPaused(Apprentice persisted)
    {
        Assert.Equal(ApprenticeStatus.Paused.ToString(), persisted.Status);

        Assert.Equal(1, persisted.CurrentStep);

        Assert.Equal("completed", ApprenticeRepository.DeserializePlan(persisted.Plan)[0].Status);

        Assert.Equal("step complete", ApprenticeRepository.DeserializePlan(persisted.Plan)[0].Result);

        Assert.Equal(1, ApprenticeRepository.DeserializeCheckpoint(persisted.CheckpointData)?.CurrentStep);
    }

    private static async Task AssertResumeDoesNotRerunTheStepAsync(
        ApprenticeService service,
        CancellationSensitiveRepository repo,
        StepThenShiftingFateIntelligence intelligence,
        Guid apprenticeId)
    {
        Result<string> resumed = await service.ResumeAsync(apprenticeId, CancellationToken.None);

        Assert.True(resumed.IsSuccess, resumed.IsFailure ? resumed.Error.Message : null);

        await WaitUntilAsync(() => !GetActiveTasks(service).ContainsKey(apprenticeId));

        Assert.Equal(1, intelligence.StreamCalls);

        Assert.Equal(ApprenticeStatus.Completed.ToString(), repo.Get(apprenticeId).Status);
    }

    /// <summary>
    /// Cancels the exact execution lease Pause, Cancel, and host shutdown cancel, from inside a provider
    /// call, so the cancellation lands at a precise point of the step rather than racing it.
    /// </summary>
    private static void CancelExecutionLease(ApprenticeService service, Guid apprenticeId)
    {
        FieldInfo? field = typeof(ApprenticeService)
            .GetField("_executionTokens", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(field);

        System.Collections.IDictionary leases =
            Assert.IsAssignableFrom<System.Collections.IDictionary>(field!.GetValue(service));

        object lease = Assert.IsAssignableFrom<object>(leases[apprenticeId]);

        PropertyInfo? cts = lease.GetType().GetProperty("Cts");

        Assert.NotNull(cts);

        Assert.IsType<CancellationTokenSource>(cts!.GetValue(lease)).Cancel();
    }

    private static Apprentice CloneApprentice(Apprentice source) => new()
    {
        Id = source.Id,
        CampaignId = source.CampaignId,
        Name = source.Name,
        Goal = source.Goal,
        Plan = source.Plan,
        CurrentStep = source.CurrentStep,
        Status = source.Status,
        SessionId = source.SessionId,
        WorkspacePath = source.WorkspacePath,
        CheckpointData = source.CheckpointData,
        ErrorMessage = source.ErrorMessage,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        ParentApprenticeId = source.ParentApprenticeId,
    };

    /// <summary>
    /// One successful step stream followed by a Shifting Fate evaluation that honours its token the way
    /// a real provider call does.
    /// </summary>
    private sealed class StepThenShiftingFateIntelligence(
        Action? afterStreamResult,
        Action? duringShiftingFate) : IArcanumIntelligenceProvider
    {
        private int _streamCalls;

        private int _shiftingFateCalls;

        internal int StreamCalls => Volatile.Read(ref _streamCalls);

        internal int ShiftingFateCalls => Volatile.Read(ref _shiftingFateCalls);

        public Task<Result<PromptTurnResult>> ExecutePromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = Interlocked.Increment(ref _shiftingFateCalls);

            duringShiftingFate?.Invoke();

            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<Result<PromptTurnResult>>(
                new PromptTurnResult("NO_CHANGE", Usage: null));
        }

        public async IAsyncEnumerable<IntelligenceEvent> StreamPromptAsync(
            PingRequest request,
            ArcanumInvocationContext invocationContext,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            InferenceAuditContext? auditContext = null)
        {
            _ = Interlocked.Increment(ref _streamCalls);

            cancellationToken.ThrowIfCancellationRequested();

            await Task.Yield();

            yield return new IntelligenceEvent(
                IntelligenceEventType.Result,
                "step complete");

            afterStreamResult?.Invoke();
        }
    }
}
