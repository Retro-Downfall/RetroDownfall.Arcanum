using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Currency drift. A completed sweep is replayed while it still describes the installation, and a request that finds it no longer does
// starts a new generation: it is never answered LongRest.Unavailable for a sweep it could simply have replaced. And the Covenant
// availability counter, which moves on every publication (a maintenance pass that removed nothing, a Covenant write at a turn's
// commit), is not what makes a sweep stale: only a change to what the sweep stands on is.
public sealed partial class LongRestMaintenanceTests
{
    private static async Task<LongRunningOperationDto> CompleteCleanSweepAsync(MaintenanceFixture f)
    {
        LongRunningOperationDto requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        SummaryOrDecision(f);

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(requested.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(requested.Id))!.State);

        return requested;
    }

    private static ProviderSettings UnrelatedProvider() => new()
    {
        Name = "unrelated",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://unrelated.test/v1",
        Models = ["other-model"],
        ContextWindowLimit = 64000,
    };

    [Theory]
    [InlineData("provider")]
    [InlineData("pricing")]
    public async Task A_completed_sweep_gives_way_to_a_new_generation_after_a_setting_the_recipe_does_not_cover_changes(string edit)
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRunningOperationDto completed = await CompleteCleanSweepAsync(f);

        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        if (edit == "provider") edited.Providers = [.. edited.Providers, UnrelatedProvider()];
        else edited.Cost.Pricing.DefaultPricing = new() { InputPer1M = 9m, OutputPer1M = 3m };

        f.Options.Set(edited);

        Result<LongRunningOperationDto> again = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : null);

        Assert.NotEqual(completed.Id, again.Value.Id);

        // The old sweep keeps its evidence and its terminal state; the new one is a new generation waiting for the worker.
        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(completed.Id))!.State);

        Assert.Equal(LongRunningOperationState.Pending, again.Value.State);

        SummaryOrDecision(f);

        LongRestSweepProgress next = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(again.Value.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, next.Phase);

        // And that one is replayed in turn, with no writes.
        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.Equal(again.Value.Id, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).Id);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));
    }

    [Fact]
    public async Task A_completed_sweep_gives_way_to_a_new_generation_after_a_maintenance_window_in_the_same_boot()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRunningOperationDto completed = await CompleteCleanSweepAsync(f);

        // A drained maintenance window moves the admission generation without changing a byte of the corpus.
        await CleanReviewSafeForeignEdgeAsync(f.Admission);

        Result<LongRunningOperationDto> again = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : null);

        Assert.NotEqual(completed.Id, again.Value.Id);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(completed.Id))!.State);
    }

    [Fact]
    public async Task A_completed_protected_sweep_gives_way_to_a_new_generation_for_a_turn_after_a_setting_the_recipe_does_not_cover_changes()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        SummaryOrDecision(f);

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);

        ArcanumSettings edited = LongRestConfigurationEpochTests.Settings();

        edited.Providers = [.. edited.Providers, UnrelatedProvider()];

        f.Options.Set(edited);

        await using CovenantTurnLease turn = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> again = await f.Coordinator.RequestAsync(f.Scope, f.Partition, turn, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : null);

        Assert.NotEqual(f.Operation, again.Value.Id);

        Assert.Equal(LongRunningOperationState.Pending, again.Value.State);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task A_completed_sweep_is_replayed_after_a_Covenant_availability_publication_that_changed_nothing()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false);

        LongRunningOperationDto completed = await CompleteCleanSweepAsync(f);

        long before = f.Runtime.Current.Availability.Generation;

        // A Covenant maintenance pass that removed and applied nothing still republishes, and so does every Covenant write.
        _ = f.Runtime.PublishAvailability(current => current with { CanonicalSequence = current.CanonicalSequence + 1 });

        _ = f.Runtime.PublishAvailability(current => current);

        Assert.True(f.Runtime.Current.Availability.Generation > before);

        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Result<LongRunningOperationDto> again = await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None);

        Assert.True(again.IsSuccess, again.IsFailure ? again.Error.Code : null);

        Assert.Equal(completed.Id, again.Value.Id);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));
    }

    [Fact]
    public async Task A_clean_sweep_that_spans_a_Covenant_availability_publication_completes_in_the_same_run()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false);

        LongRunningOperationDto requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        bool published = false;

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            if (!published)
            {
                published = true;

                _ = f.Runtime.PublishAvailability(current => current with { CanonicalSequence = current.CanonicalSequence + 1 });
            }

            return Task.CompletedTask;
        };

        Result<LongRestSweepProgress> run = await f.ResumeAdmittedAsync(requested.Id, CancellationToken.None);

        Assert.True(published);

        LongRestSweepProgress progress = LongRestSweepHarness.Require(run);

        Assert.Equal(LongRestSweepPhase.Completed, progress.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(requested.Id))!.State);
    }

    [Fact]
    public async Task A_protected_sweep_parked_for_a_fresh_request_is_readmitted_after_a_Covenant_availability_publication()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        f.Handler.Status = System.Net.HttpStatusCode.InternalServerError;

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, parked.DeferredReason);

        // The turn that parked it commits a Covenant write, and a maintenance pass republishes after it.
        _ = f.Runtime.PublishAvailability(current => current with { CanonicalSequence = current.CanonicalSequence + 1 });

        _ = f.Runtime.PublishAvailability(current => current);

        // The next eligible Session turn takes a fresh lease, finds the park returned unchanged, and readmits it.
        await using CovenantTurnLease next = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, next, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.Equal(f.Operation, request.Value.Id);

        f.Handler.Status = System.Net.HttpStatusCode.OK;

        SummaryOrDecision(f);

        Result<LongRestMaintenanceCapability> capability = await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, next, CancellationToken.None);

        Assert.True(capability.IsSuccess, capability.IsFailure ? capability.Error.Code : null);

        LongRestSweepProgress done = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, capability.Value, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, done.Phase);
    }

    [Fact]
    public async Task A_protected_park_whose_fence_changed_in_its_own_boot_is_returned_but_cannot_be_readmitted_by_the_turn()
    {
        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: true);

        f.Handler.Status = System.Net.HttpStatusCode.InternalServerError;

        LongRestSweepProgress parked = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, await FreshAsync(f), CancellationToken.None));

        Assert.Equal(LongRestParkCodes.AuthorizedRequestRequired, parked.DeferredReason);

        // A drained maintenance window inside the boot that parked it: only a new boot readmits across it.
        await CleanReviewSafeForeignEdgeAsync(f.Admission);

        await using CovenantTurnLease next = LongRestSweepHarness.Require(await f.Gate.AcquireTurnAsync(f.Scope, CancellationToken.None));

        Result<LongRunningOperationDto> request = await f.Coordinator.RequestAsync(f.Scope, f.Partition, next, CancellationToken.None);

        Assert.True(request.IsSuccess, request.IsFailure ? request.Error.Code : null);

        Assert.Equal(f.Operation, request.Value.Id);

        Result<LongRestMaintenanceCapability> capability = await f.Maintenance.CreateCapabilityAsync(f.Operation, f.Invocation, f.Claim, f.Scope, next, CancellationToken.None);

        Assert.True(capability.IsFailure);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, capability.Error.Code);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);
    }

    [Fact]
    public async Task A_Covenant_availability_change_that_matters_still_ends_the_sweep_it_spans()
    {
        ManualWallClockTimeProvider clock = new();

        await using MaintenanceFixture f = await MaintenanceFixture.CreateAsync(ownWorker: false, protectedSources: false, clock: clock);

        LongRunningOperationDto requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            _ = f.Runtime.PublishAvailability(current => current with { FeatureEnabled = false });

            return Task.CompletedTask;
        };

        Result<LongRestSweepProgress> run = await f.ResumeAdmittedAsync(requested.Id, CancellationToken.None);

        Assert.True(run.IsFailure || run.Value.Phase != LongRestSweepPhase.Completed);

        Assert.NotEqual(LongRunningOperationState.Completed, (await f.Operations.GetAsync(requested.Id))!.State);

        // The worker's readmission finds the fence changed inside its own boot, which no readmission survives: the sweep is settled
        // for the next request to replace.
        clock.Advance(LongRestFaultPolicy.Cooldown(0));

        _ = await f.ResumeAdmittedAsync(requested.Id, CancellationToken.None);

        LongRunningOperation ended = (await f.Operations.GetAsync(requested.Id))!;

        Assert.Equal(LongRunningOperationState.Abandoned, ended.State);

        Assert.Equal(ErrorCodes.LongRest.StaleInput, ended.TerminalErrorCode);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = DecisionOrSummary(f.Handler.LastBody!);

            return Task.CompletedTask;
        };

        _ = f.Runtime.PublishAvailability(current => current with { FeatureEnabled = true });

        LongRunningOperationDto replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        Assert.NotEqual(requested.Id, replacement.Id);
    }
}
