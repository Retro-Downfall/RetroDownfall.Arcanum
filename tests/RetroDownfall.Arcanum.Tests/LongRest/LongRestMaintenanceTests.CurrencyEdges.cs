using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
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
    public async Task A_generation_admitted_after_the_newest_tracked_memory_is_deleted_freezes_the_highest_sequence_ever_issued()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        // Finish the fixture's sweep first. Its generation froze its boundary before the memory elsewhere existed, and a request would otherwise only join it.
        _ = await CompleteCleanSweepAsync(f);

        Guid elsewhere = await SeedElsewhereAsync(f);

        long issued = (long)(await f.H.ScalarAsync("SELECT seq FROM sqlite_sequence WHERE name = 'long_rest_sources';"))!;

        Assert.True(await f.H.Saga.Store.DeleteAsync(elsewhere.ToString("D"), CancellationToken.None));

        // The highest row fell with the deletion; the highest sequence ever issued did not.
        Assert.True(issued > (long)(await f.H.ScalarAsync("SELECT max(InsertionSequence) FROM long_rest_sources;"))!);

        // The replacement is admitted, gets its generation and completes after the deletion.
        LongRunningOperationDto replacement = await ReplaceCleanSweepWithPricingAsync(f, 9m);

        // The boundary is the highest sequence ever issued, not the highest row that is left, so a memory seeded from now on lies above it and is debt for the next generation.
        Assert.Equal(
            issued,
            await f.H.ScalarAsync($"SELECT InsertionHighWatermark FROM long_rest_discovery_generations WHERE OperationId = '{replacement.Id:N}';"));
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

    // A run completes the generation and, in later writes, settles the operation (the checkpoint, then the transition). A request that
    // arrives in between finds a sweep whose generation is complete and whose operation is still running: it is the sweep that describes the
    // installation, and the request joins it instead of paying for a generation that would only supersede it.

    /// <summary>A coordinator whose run is hooked at the moment its sweep's Completed checkpoint is durable, which is before it settles the operation.</summary>
    private static LongRestSweepCoordinator CompletingCoordinator(MaintenanceFixture f, Func<Task> atCompletion) =>
        new(new HookedLongRunningOperationStore(f.Operations)
        {
            AfterCheckpointSaved = async (id, payload) =>
            {
                if (payload is not null && LongRestSweepCheckpointCodec.Decode(id, payload) is { IsSuccess: true } decoded
                    && decoded.Value.Phase == LongRestSweepPhase.Completed)
                {
                    await atCompletion();
                }
            },
        }, new LongRestSweepStore(f.H.Saga.Context, f.Clock), f.H.Store, f.Epoch, f.Facts, f.Maintenance, f.Gate, new LongRunningOperationOwnership(),
        new LongRestSweepQueue(), f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, f.RecordedAdmission);

    [Fact]
    public async Task A_request_that_arrives_between_a_generation_completing_and_its_operation_settling_joins_the_sweep()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        Result<LongRunningOperationDto>? raced = null;

        long sweeps = 0;

        LongRestSweepCoordinator running = CompletingCoordinator(f, async () =>
        {
            // The generation and the sweep are Completed; the operation has not left Running.
            Assert.Equal((long)LongRestSweepPhase.Completed, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

            Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

            sweeps = (long)(await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"))!;

            raced = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);
        });

        SummaryOrDecision(f);

        Assert.True(f.RecordedAdmission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted));

        await using (IGrimoireWorkLease lease = admitted!)
        {
            Assert.Equal(LongRestSweepPhase.Completed, LongRestSweepHarness.Require(await running.ResumeAdmittedAsync(f.Operation, lease, CancellationToken.None)).Phase);
        }

        Assert.NotNull(raced);

        Result<LongRunningOperationDto> answer = raced;

        Assert.True(answer.IsSuccess, answer.IsFailure ? answer.Error.Code : null);

        // The sweep that was finishing, and no third generation beside it.
        Assert.Equal(f.Operation, answer.Value.Id);

        Assert.Equal(LongRunningOperationState.Running, answer.Value.State);

        Assert.Equal(sweeps, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        Assert.Equal(1L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        // Once it has settled it is the replay, with no write.
        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> later = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(later.IsSuccess, later.IsFailure ? later.Error.Code : null);

        Assert.Equal(f.Operation, later.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, later.Value.State);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));
    }

    /// <summary>A protected sweep whose generation completed and whose host died before its operation settled, left parked by recovery.</summary>
    private static async Task<LongRestMaintenanceCapability> ParkProtectedAfterCompletingAsync(MaintenanceFixture f)
    {
        SummaryOrDecision(f);

        LongRestMaintenanceCapability capability = await FreshAsync(f);

        await ParkAfterCompletingAsync(f, capability, LongRestParkCodes.AuthorizedRequestRequired);

        return capability;
    }

    /// <summary>A sweep whose generation completed and whose host died before its operation settled, left parked by recovery under the given code.</summary>
    private static async Task ParkAfterCompletingAsync(MaintenanceFixture f, LongRestMaintenanceCapability? capability, string parkCode)
    {
        LongRestSweepCoordinator dying = CompletingCoordinator(f, () => throw new IOException("The host died before the operation settled."));

        IOException died = await Assert.ThrowsAsync<IOException>(() => dying.ResumeAsync(f.Operation, capability, CancellationToken.None));

        Assert.Equal("The host died before the operation settled.", died.Message);

        Assert.Equal((long)LongRestSweepPhase.Completed, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal((long)LongRestSweepPhase.Completed, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal(LongRunningOperationState.Running, (await f.Operations.GetAsync(f.Operation))!.State);

        // The lease lapses and recovery parks the operation: nobody has the sweep, and it is a protected one.
        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $operation AND LeaseOwner IS NOT NULL;",
            ("$operation", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new LongRestSweepRecoveryHandler()], f.Clock,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        Assert.Equal(1, (await reconciler.ReconcileNowAsync("completion-crash-recovery")).RequiresAttention);

        LongRunningOperation parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Equal(parkCode, parked.TerminalErrorCode);
    }

    [Fact]
    public async Task A_protected_sweep_parked_after_its_generation_completed_is_joined_by_the_next_turn_and_completed_without_a_third_operation()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedAfterCompletingAsync(f);

        long sweeps = (long)(await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"))!;

        // No request returned it before: the replay wants a Completed operation and the active sweep excludes a completed generation.
        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> joined = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(joined.IsSuccess, joined.IsFailure ? joined.Error.Code : null);

        Assert.Equal(f.Operation, joined.Value.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, joined.Value.State);

        Assert.Equal(sweeps, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(
            await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, turn, CancellationToken.None));

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, capability, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(sweeps, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        // And it is the replay from then on.
        Result<LongRunningOperationDto> later = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(later.IsSuccess, later.IsFailure ? later.Error.Code : null);

        Assert.Equal(f.Operation, later.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, later.Value.State);
    }

    [Fact]
    public async Task A_protected_sweep_parked_after_its_generation_completed_is_settled_when_it_no_longer_describes_the_installation()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedAfterCompletingAsync(f);

        // A setting the configuration value digest covers moves: the completed generation describes an installation that is gone, so nothing
        // can readmit the park, and it would hold its operation parked for ever if it were left.
        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = 9m, OutputPer1M = 3m };

        f.Options.Set(edited);

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> replacement = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(replacement.IsSuccess, replacement.IsFailure ? replacement.Error.Code : null);

        Assert.NotEqual(f.Operation, replacement.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, replacement.Value.State);

        LongRunningOperation settled = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, settled.State);

        Assert.Equal(LongRestParkCodes.StaleConfiguration, settled.TerminalErrorCode);

        Assert.Null(settled.LeaseOwner);

        Assert.NotNull(settled.CompletedAt);

        // Its sweep and generation stay as the history they are, until the generation that replaces them completes.
        Assert.Equal((long)LongRestSweepPhase.Completed, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

        Assert.Equal((long)LongRestSweepPhase.Completed, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_discovery_generations WHERE OperationId = '{f.Operation:N}';"));

        Assert.False(f.Ownership.IsClaimed(f.Operation));

        SummaryOrDecision(f);

        LongRestMaintenanceCapability capability = LongRestSweepHarness.Require(
            await f.Maintenance.CreateCapabilityAsync(replacement.Value.Id, f.Invocation, f.Claim, f.Scope, turn, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed,
            LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(replacement.Value.Id, capability, CancellationToken.None)).Phase);

        Assert.Equal((long)LongRestSweepPhase.Invalidated, await f.H.ScalarAsync($"SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));
    }

    [Fact]
    public async Task A_clean_sweep_parked_after_its_generation_completed_is_joined_while_it_describes_the_installation_and_settled_when_it_no_longer_does()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        SummaryOrDecision(f);

        await ParkAfterCompletingAsync(f, null, LongRestParkCodes.Interrupted);

        long sweeps = (long)(await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"))!;

        // The worker readmits a clean park, so while it describes the installation a request joins it and pays for no generation of its own.
        Result<LongRunningOperationDto> joined = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(joined.IsSuccess, joined.IsFailure ? joined.Error.Code : null);

        Assert.Equal(f.Operation, joined.Value.Id);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, joined.Value.State);

        Assert.Equal(sweeps, await f.H.ScalarAsync("SELECT count(*) FROM LongRunningOperations WHERE Kind = 'long-rest-sweep';"));

        // Once it describes an installation that is gone, nothing can readmit it, so the first request that finds it settles it and is answered
        // by a generation of its own.
        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = 9m, OutputPer1M = 3m };

        f.Options.Set(edited);

        Result<LongRunningOperationDto> replacement = await f.Coordinator.RequestAsync(f.Scope, f.Partition, CancellationToken.None);

        Assert.True(replacement.IsSuccess, replacement.IsFailure ? replacement.Error.Code : null);

        Assert.NotEqual(f.Operation, replacement.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, replacement.Value.State);

        LongRunningOperation settled = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, settled.State);

        Assert.Equal(LongRestParkCodes.StaleConfiguration, settled.TerminalErrorCode);

        Assert.Null(settled.LeaseOwner);

        Assert.NotNull(settled.CompletedAt);

        Assert.False(f.Ownership.IsClaimed(f.Operation));
    }

    [Fact]
    public async Task A_park_only_an_operator_can_clear_is_neither_joined_nor_settled_by_a_request()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        _ = await ParkProtectedAfterCompletingAsync(f);

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET TerminalErrorCode = 'LongRest.ProviderRejected' WHERE Id = $operation;", ("$operation", f.Operation.ToString("N")));

        async Task<string> OperatorsParkAsync() => string.Join('\n',
            await f.H.ScalarAsync($"SELECT json_array(Id, State, Revision, AttemptCount, LeaseOwner, LeaseExpiresAt, TerminalErrorCode, hex(CheckpointPayload)) FROM LongRunningOperations WHERE Id = '{f.Operation:N}';"),
            await f.H.ScalarAsync($"SELECT json_array(OperationId, GenerationId, PhaseCode, Revision, ConfigurationJson, ReadmitCount, ParkedAt, UpdatedAtUtc) FROM long_rest_sweeps WHERE OperationId = '{f.Operation:N}';"));

        string before = await OperatorsParkAsync();

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        // The operator's park is untouched, and the request is answered by a generation of its own, as it was before a sweep could be joined.
        Assert.NotEqual(f.Operation, request.Value.Id);

        Assert.Equal(before, await OperatorsParkAsync());

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);
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
            LongRunningOperationOwnership ownership, LongRestRecipeIdentity currentRecipe, CancellationToken ct,
            Func<CancellationToken, ValueTask<Result>>? proveAuthority = null, LongRestRecipeIdentity? replayRecipe = null) =>
            inner.AdmitRequestAsync(scope, fence, facts, ownership, currentRecipe, ct, proveAuthority, replayRecipe);

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
