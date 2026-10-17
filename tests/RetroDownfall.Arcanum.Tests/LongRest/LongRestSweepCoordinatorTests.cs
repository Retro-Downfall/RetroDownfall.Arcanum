using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestSweepCoordinatorTests
{
    [Fact]
    public async Task Initial_checkpoint_precedes_provider_and_completed_replay_has_no_writes()
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Clean first observation.");

        await f.H.SeedAsync(Guid.NewGuid().ToString("D"), "Clean second observation.");

        var requested = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        Assert.NotEqual(f.Operation, requested.Id);

        f.Handler.InsideSend = async () =>
        {
            var actual = await f.Operations.GetAsync(requested.Id);

            Assert.Equal(1, actual!.CheckpointVersion);

            Assert.NotNull(actual.CheckpointPayload);

            Assert.Equal(LongRunningOperationState.Running, actual.State);

            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";
        };

        LongRestSweepProgress result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(requested.Id, null, CancellationToken.None));

        Assert.Equal(LongRestSweepPhase.Completed, result.Phase);

        Assert.Equal(LongRunningOperationState.Completed, (await f.Operations.GetAsync(requested.Id))!.State);

        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        int sends = f.Handler.SendCount;

        Assert.Equal(requested.Id, LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).Id);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(sends, f.Handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_ordinary_retry_parks_without_materializing_sources_or_dispatching(bool campaign)
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false, campaign: campaign);

        LongRestSweepProgress result = LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal("long_rest.authorized_request_required", result.DeferredReason);

        var parked = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, parked.State);

        Assert.Null(parked.LeaseOwner);

        Assert.Equal(1, parked.CheckpointVersion);

        Assert.True(await f.Operations.ResetForRetryAsync(f.Operation, parked.Revision, DateTimeOffset.UtcNow));

        LongRestSweepHarness.Require(await f.Coordinator.ResumeAsync(f.Operation, null, CancellationToken.None));

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, (await f.Operations.GetAsync(f.Operation))!.State);

        Assert.Equal(0L, await f.H.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        Assert.Equal(0, f.Handler.SendCount);
    }

    [Fact]
    public async Task Disabled_request_has_no_operation_or_sweep_writes()
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        ArcanumSettings disabled = LongRestConfigurationEpochTests.Settings();

        disabled.Features.LongRest = false;

        f.Options.Set(disabled);

        long writes = (long)(await f.H.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None)).IsFailure);

        Assert.Equal(writes, await f.H.ScalarAsync("SELECT total_changes();"));
    }
}
