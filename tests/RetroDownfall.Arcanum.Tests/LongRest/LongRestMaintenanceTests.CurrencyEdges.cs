using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// The edges of currency. A completed sweep, an unfinished one and a parked one are each judged by what actually bears on them: the
// insertion boundary never falls because a memory somewhere else was deleted, a setting changed and changed back is the same setting,
// a protected park that can no longer resume is replaced by the next turn that carries live authority, and a request that raced
// the completion of its own replacement joins that replacement instead of starting a third generation.
public sealed partial class LongRestMaintenanceTests
{
    private static async Task<LongRunningOperationDto> ReplaceCleanSweepWithPricingAsync(MaintenanceFixture f, decimal input)
    {
        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = input, OutputPer1M = 3m };

        f.Options.Set(edited);

        LongRunningOperationDto replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        SummaryOrDecision(f);

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(replacement.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);

        return replacement;
    }

    private static async Task<Guid> SeedElsewhereAsync(MaintenanceFixture f)
    {
        Guid elsewhere = Guid.NewGuid();

        await f.H.SeedAsync(elsewhere.ToString("D"), "Gamma observation elsewhere.", await f.H.SessionAsync(Guid.NewGuid()));

        return elsewhere;
    }

    [Fact]
    public async Task A_completed_sweep_is_replayed_after_the_newest_memory_of_another_scope_is_deleted()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        _ = await CompleteCleanSweepAsync(f);

        Guid elsewhere = await SeedElsewhereAsync(f);

        // The replacement generation freezes a boundary that includes the memory elsewhere, which is not one of its leaves.
        LongRunningOperationDto second = await ReplaceCleanSweepWithPricingAsync(f, 9m);

        Assert.Equal(second.Id, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).Id);

        Assert.True(await f.H.Saga.Store.DeleteAsync(elsewhere.ToString("D"), CancellationToken.None));

        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> after = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(after.IsSuccess, after.IsFailure ? after.Error.Code + ": " + after.Error.Message : null);

        Assert.Equal(second.Id, after.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, after.Value.State);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));
    }

    [Fact]
    public async Task A_completed_protected_sweep_is_replayed_for_a_turn_after_the_newest_memory_of_another_scope_is_deleted()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        SummaryOrDecision(f);

        Assert.Equal(LongRestSweepPhase.Completed,
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None)).Phase);

        Guid elsewhere = await SeedElsewhereAsync(f);

        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = 9m, OutputPer1M = 3m };

        f.Options.Set(edited);

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        LongRunningOperationDto second = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None));

        Assert.NotEqual(f.Operation, second.Id);

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(
            await f.Maintenance.CreateCapabilityAsync(second.Id, f.Invocation, f.Claim, f.Scope, turn, CancellationToken.None));

        SummaryOrDecision(f);

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(second.Id, capability, CancellationToken.None)).Phase);

        Assert.True(await f.H.Saga.Store.DeleteAsync(elsewhere.ToString("D"), CancellationToken.None));

        Result<LongRunningOperationDto> after = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(after.IsSuccess, after.IsFailure ? after.Error.Code + ": " + after.Error.Message : null);

        Assert.Equal(second.Id, after.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, after.Value.State);
    }

    [Fact]
    public async Task A_sweep_in_flight_is_readmitted_after_the_newest_memory_of_another_scope_is_deleted()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        _ = await CompleteCleanSweepAsync(f);

        Guid elsewhere = await SeedElsewhereAsync(f);

        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = 9m, OutputPer1M = 3m };

        f.Options.Set(edited);

        LongRunningOperationDto second = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        f.Handler.Status = System.Net.HttpStatusCode.InternalServerError;

        _ = await f.ResumeAdmittedAsync(second.Id, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(second.Id))!.State);

        Assert.True(await f.H.Saga.Store.DeleteAsync(elsewhere.ToString("D"), CancellationToken.None));

        f.Handler.Status = System.Net.HttpStatusCode.OK;

        SummaryOrDecision(f);

        clock.Advance(LongRestFaultPolicy.Cooldown(0) + TimeSpan.FromSeconds(1));

        Result<LongRestSweepProgress> resumed = await f.ResumeAdmittedAsync(second.Id, CancellationToken.None);

        LongRunningOperation after = (await f.Operations.GetAsync(second.Id))!;

        Assert.True(resumed.IsSuccess && resumed.Value.Phase == LongRestSweepPhase.Completed,
            $"{(resumed.IsFailure ? resumed.Error.Code : resumed.Value.Phase + "/" + resumed.Value.DeferredReason)}; operation {after.State} {after.TerminalErrorCode}");
    }

    [Fact]
    public async Task A_request_is_admitted_and_its_sweep_completes_after_the_newest_tracked_memory_is_deleted()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        Guid elsewhere = await SeedElsewhereAsync(f);

        Assert.True(await f.H.Saga.Store.DeleteAsync(elsewhere.ToString("D"), CancellationToken.None));

        LongRunningOperationDto requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        SummaryOrDecision(f);

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(requested.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);

        // The boundary is the highest sequence ever issued, so a memory seeded after the deletion lies above it and is debt for the next generation.
        long frozen = (long)(await f.H.ScalarAsync("SELECT InsertionHighWatermark FROM long_rest_discovery_generations;"))!;

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Delta observation.", f.SourceSession);

        Assert.True((long)(await f.H.ScalarAsync("SELECT max(InsertionSequence) FROM long_rest_sources;"))! > frozen);
    }

    [Fact]
    public async Task A_completed_sweep_is_replayed_after_a_setting_is_changed_and_changed_back_in_one_boot()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        LongRunningOperationDto completed = await CompleteCleanSweepAsync(f);

        ArcanumSettings off = LongRestConfigurationEpochTests.Settings();

        off.Features.LongRest = false;

        f.Options.Set(off);

        f.Options.Set(LongRestConfigurationEpochTests.Settings());

        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> again = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : null);

        Assert.Equal(completed.Id, again.Value.Id);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));
    }

    [Fact]
    public async Task A_completed_protected_sweep_is_replayed_for_a_turn_after_a_setting_is_changed_and_changed_back_in_one_boot()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        SummaryOrDecision(f);

        Assert.Equal(LongRestSweepPhase.Completed,
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None)).Phase);

        ArcanumSettings off = LongRestConfigurationEpochTests.Settings();

        off.Features.LongRest = false;

        f.Options.Set(off);

        f.Options.Set(LongRestConfigurationEpochTests.Settings());

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> again = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : null);

        Assert.Equal(f.Operation, again.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, again.Value.State);
    }

    [Fact]
    public async Task A_parked_sweep_does_not_survive_a_setting_changed_and_changed_back_in_one_boot()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        f.Handler.Status = System.Net.HttpStatusCode.InternalServerError;

        _ = await f.ResumeAdmittedAsync(f.Operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);

        ArcanumSettings off = LongRestConfigurationEpochTests.Settings();

        off.Features.LongRest = false;

        f.Options.Set(off);

        f.Options.Set(LongRestConfigurationEpochTests.Settings());

        // The epoch is what protects work in flight, so it still ends a sweep that had not completed: only a completed sweep stops asking.
        LongRunningOperationDto again = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        Assert.NotEqual(f.Operation, again.Id);

        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(LongRestParkCodes.StaleConfiguration, retired.TerminalErrorCode);
    }

    [Fact]
    public async Task A_protected_park_whose_fence_changed_in_its_own_boot_is_replaced_by_the_turn_that_carries_authority()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        f.Handler.Status = System.Net.HttpStatusCode.InternalServerError;

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, parked.DeferredReason);

        // A drained maintenance window inside the boot that parked it: no readmission survives it, because a protected sweep has no
        // attestation that the window was a clean one.
        await CleanReviewSafeForeignEdgeAsync(f.Admission);

        // Without authority nothing can retire a protected sweep: the park is returned unchanged for a turn to deal with.
        Result<LongRunningOperationDto> unauthorized = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(unauthorized.IsSuccess, unauthorized.IsFailure ? unauthorized.Error.Code : null);

        Assert.Equal(f.Operation, unauthorized.Value.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);

        await using CovenantTurnLease next = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, next, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.NotEqual(f.Operation, request.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, request.Value.State);

        LongRunningOperation retired = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, retired.State);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, retired.TerminalErrorCode);

        // The turn goes on to finish the partition under the new generation.
        f.Handler.Status = System.Net.HttpStatusCode.OK;

        SummaryOrDecision(f);

        Result<LongRestMaintenanceCapability> capability = await f.Maintenance.CreateCapabilityAsync(request.Value.Id, f.Invocation, f.Claim, f.Scope, next, CancellationToken.None);

        Assert.True(capability.IsSuccess, capability.IsFailure ? capability.Error.Code : null);

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(request.Value.Id, capability.Value, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);
    }

    [Fact]
    public async Task A_protected_park_that_can_still_resume_is_returned_to_the_turn_unchanged()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        f.Handler.Status = System.Net.HttpStatusCode.InternalServerError;

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired,
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None)).DeferredReason);

        // A publication that changes no capability fact leaves the fence equal, so the park is the turn's to readmit.
        _ = f.Runtime.PublishAvailability(current => current with { CanonicalSequence = current.CanonicalSequence + 1 });

        await using CovenantTurnLease next = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, next, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.Equal(f.Operation, request.Value.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task A_request_whose_candidate_was_superseded_after_the_lookup_joins_the_successor()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        LongRunningOperationDto first = await CompleteCleanSweepAsync(f);

        LongRestSweepStore sweeps = new(f.H.Saga.Context, f.Clock);

        // This request's lookup found the first sweep while it was still the newest completed one.
        Guid? found = LongRestSweepHarness.Require(await sweeps.FindCompletedRequestReplayAsync(f.Scope, f.Partition,
            ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, CancellationToken.None));

        Assert.Equal(first.Id, found);

        // Another request then replaced it, and the replacement completed before this one reached admission.
        LongRunningOperationDto successor = await ReplaceCleanSweepWithPricingAsync(f, 9m);

        LongRestSweepCoordinator racing = Racing(f, sweeps, found);

        Result<LongRunningOperationDto> request = await racing.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.Equal(successor.Id, request.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, request.Value.State);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));
    }

    [Fact]
    public async Task A_protected_request_whose_candidate_was_superseded_after_the_lookup_joins_the_successor()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        SummaryOrDecision(f);

        Assert.Equal(LongRestSweepPhase.Completed,
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None)).Phase);

        LongRestSweepStore sweeps = new(f.H.Saga.Context, f.Clock);

        Guid? found = LongRestSweepHarness.Require(await sweeps.FindCompletedRequestReplayAsync(f.Scope, f.Partition,
            ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, CancellationToken.None));

        Assert.Equal(f.Operation, found);

        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = 9m, OutputPer1M = 3m };

        f.Options.Set(edited);

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        LongRunningOperationDto successor = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None));

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(
            await f.Maintenance.CreateCapabilityAsync(successor.Id, f.Invocation, f.Claim, f.Scope, turn, CancellationToken.None));

        SummaryOrDecision(f);

        Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(successor.Id, capability, CancellationToken.None)).Phase);

        LongRestSweepCoordinator racing = Racing(f, sweeps, found);

        Result<LongRunningOperationDto> request = await racing.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.Equal(successor.Id, request.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, request.Value.State);

        Assert.Equal(2L, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));
    }

    [Fact]
    public async Task A_candidate_invalidated_after_the_lookup_is_not_replayed()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        LongRunningOperationDto first = await CompleteCleanSweepAsync(f);

        LongRestSweepStore sweeps = new(f.H.Saga.Context, f.Clock);

        Guid? found = LongRestSweepHarness.Require(await sweeps.FindCompletedRequestReplayAsync(f.Scope, f.Partition,
            ((ILongRestConfigurationProvider)f.Epoch).Capture().Recipe.Value, CancellationToken.None));

        Assert.Equal(first.Id, found);

        // A source the sweep froze is deleted after the lookup: the sweep is invalidated, though no fence term moved.
        Assert.True(await f.H.Saga.Store.DeleteAsync(f.Source, CancellationToken.None));

        LongRestSweepCoordinator racing = Racing(f, sweeps, found);

        Result<LongRunningOperationDto> request = await racing.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.NotEqual(first.Id, request.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, request.Value.State);
    }

    /// <summary>A coordinator over the fixture whose completed-sweep lookup answers with a candidate found earlier, as a request that raced does.</summary>
    private static LongRestSweepCoordinator Racing(MaintenanceFixture f, LongRestSweepStore sweeps, Guid? candidate) =>
        new(f.Operations, new StaleLookupSweeps(sweeps, candidate), f.H.Store, f.Epoch, f.Facts, f.Maintenance, f.Gate, f.Ownership,
            new LongRestSweepQueue(), f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, f.RecordedAdmission);

    private sealed class StaleLookupSweeps(LongRestSweepStore inner, Guid? candidate) : ILongRestSweepStore, ILongRestCompletedRequestReplay, ILongRestSweepRequestAdmission
    {
        public Task<Result<Guid?>> FindCompletedRequestReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition,
            LongRestRecipeIdentity recipe, CancellationToken ct) => Task.FromResult(Result<Guid?>.Success(candidate));

        public Task<Result<LongRunningOperation>> AdmitRequestAsync(LongRestSweepScope scope, LongRestSweepFence fence, LongRestLiveFenceReader facts,
            LongRunningOperationOwnership ownership, CancellationToken ct, Func<CancellationToken, ValueTask<Result>>? proveAuthority = null,
            LongRestRecipeIdentity? replayRecipe = null) => inner.AdmitRequestAsync(scope, fence, facts, ownership, ct, proveAuthority, replayRecipe);

        public Task<Result<LongRestRunnableSweep>> FindNextRunnableCleanAsync(LongRestConfigurationStamp configuration, Guid? afterOperationId, CancellationToken ct) =>
            inner.FindNextRunnableCleanAsync(configuration, afterOperationId, ct);

        public Task<Result<LongRestSweep>> BeginOrResumeAsync(Guid operationId, LongRestSweepScope scope, LongRestConfigurationStamp configuration, CancellationToken ct) =>
            inner.BeginOrResumeAsync(operationId, scope, configuration, ct);

        public Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestSweepScope partition, LongRestRecipeIdentity recipe, CancellationToken ct) =>
            inner.FindCompletedReplayAsync(scope, partition, recipe, ct);

        public Task<Result<Guid?>> FindCompletedReplayAsync(CanonicalCampaignContext scope, LongRestRecipeIdentity recipe, CancellationToken ct) =>
            inner.FindCompletedReplayAsync(scope, recipe, ct);

        public Task<Result<LongRestSweepWork?>> ReadNextWorkAsync(LongRestSweep expected, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            inner.ReadNextWorkAsync(expected, capability, ct);

        public Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            inner.CommitAcceptedAdjudicationAsync(expected, accepted, capability, ct);

        public Task<Result<LongRestAdjudication>> CommitAcceptedAdjudicationAsync(LongRestSweep expected, LongRestAdjudication accepted, LongRestDispatchCompletion completion,
            string workerOwner, LongRestMaintenanceCapability? capability, CancellationToken ct, bool committedOnly = false) =>
            inner.CommitAcceptedAdjudicationAsync(expected, accepted, completion, workerOwner, capability, ct, committedOnly);

        public Task<Result<LongRestSweep>> CommitExactDuplicatesAsync(LongRestSweep expected, LongRestWorkIdentity work, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            inner.CommitExactDuplicatesAsync(expected, work, capability, ct);

        public Task<Result<LongRestSweep>> CommitDecisionAsync(LongRestSweep expected, LongRestAdjudication decision, LongRestMaintenanceCapability? capability, CancellationToken ct) =>
            inner.CommitDecisionAsync(expected, decision, capability, ct);
    }
}
