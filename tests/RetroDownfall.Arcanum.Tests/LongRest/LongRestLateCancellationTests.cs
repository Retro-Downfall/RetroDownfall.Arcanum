using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestLateCancellationTests
{
    [Fact]
    public async Task Cancellation_after_durable_sweep_completion_does_not_shadow_a_fresh_request()
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: true, protectedSources: false);

        f.Handler.InsideSend = () =>
        {
            f.Handler.ChatText = LongRestScriptedVerdicts.IsAdjudication(f.Handler.LastBody!)
                ? LongRestScriptedVerdicts.NoMatches(f.Handler.LastBody!) : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        // Recovery parks the interrupted sweep. The worker then readmits it and completes every durable stage, and the
        // operator's cancellation lands after the final checkpoint but before the final operation transition.
        await LongRestMaintenanceTests.ParkExpiredSweepAsync(f, "completion-cancellation-fixture-owner");

        FixedCovenantConnectionSource connections = new(f.H.Connection);

        // The operator's cancellation lands the moment the sweep's final Completed checkpoint is durable, before any transition.
        HookedLongRunningOperationStore operations = new(f.Operations)
        {
            AfterCheckpointSaved = async (id, payload) =>
            {
                if (payload is null || LongRestSweepCheckpointCodec.Decode(id, payload) is not { IsSuccess: true } decoded
                    || decoded.Value.Phase != LongRestSweepPhase.Completed) return;

                LongRunningOperation latest = (await f.Operations.GetAsync(id))!;

                Assert.True(await f.Operations.RequestCancellationAsync(id, latest.Revision, DateTimeOffset.UtcNow));
            },
        };

        LongRestSweepCoordinator coordinator = new(operations,
            new LongRestSweepStore(f.H.Saga.Context, f.Clock), f.H.Store, f.Epoch,
            new LongRestLiveFenceReader(f.H.Saga.Context, f.Runtime, f.RecordedAdmission, f.Epoch,
                new CampaignAvailabilityReader(connections), new CampaignPathIdentityReader(connections, null!)),
            f.Maintenance, f.Gate, new LongRunningOperationOwnership(), new LongRestSweepQueue(),
            f.Services.GetRequiredService<IServiceScopeFactory>(), f.Clock, f.RecordedAdmission);

        Assert.True(f.RecordedAdmission.TryAcquireWorkLease(GrimoireWorkKind.LongRestSweep, out IGrimoireWorkLease? admitted));

        await using (IGrimoireWorkLease lease = admitted!)
        {
            LongRestSweepProgress progress = LongRestSweepHarness.Require(await coordinator.ResumeAdmittedAsync(f.Operation, lease, CancellationToken.None));

            Assert.Equal(LongRestParkCodes.Cancelled, progress.DeferredReason);
        }

        LongRunningOperation terminal = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, terminal.State);

        Assert.Equal("long_rest.cancelled", terminal.TerminalErrorCode);

        LongRestDiscoveryGeneration generation = LongRestSweepHarness.Require(await f.H.Store.ReadGenerationMetadataAsync(f.Operation, CancellationToken.None))!;

        Assert.Equal(LongRestSweepPhase.Completed, generation.Phase);

        Assert.Equal(6L, await f.H.ScalarAsync("SELECT PhaseCode FROM long_rest_sweeps WHERE OperationId = '" + f.Operation.ToString("N") + "';"));

        var replacement = LongRestSweepHarness.Require(await f.Coordinator.RequestAsync(f.Scope, CancellationToken.None));

        Assert.NotEqual(terminal.Id, replacement.Id);

        AssertOperationSnapshot(terminal, (await f.Operations.GetAsync(terminal.Id))!);
    }

    private static void AssertOperationSnapshot(LongRunningOperation expected, LongRunningOperation actual)
    {
        // The only array field is compared by content; all remaining record fields remain exact.
        Assert.Equal(expected with { CheckpointPayload = null }, actual with { CheckpointPayload = null });

        Assert.Equal(expected.CheckpointPayload is null, actual.CheckpointPayload is null);

        if (expected.CheckpointPayload is not null)
        {
            Assert.Equal<byte>(expected.CheckpointPayload, actual.CheckpointPayload!);
        }
    }
}
