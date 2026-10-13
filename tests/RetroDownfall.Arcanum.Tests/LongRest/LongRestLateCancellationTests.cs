using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

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
            f.Handler.ChatText = f.Handler.LastBody!.Contains("survivorVersionId", StringComparison.Ordinal)
                ? "{\"kind\":\"NoChange\",\"survivorVersionId\":null}" : "{\"summary\":\"Accepted summary.\"}";

            return Task.CompletedTask;
        };

        await f.H.ExecuteAsync("UPDATE LongRunningOperations SET LeaseExpiresAt = '2000-01-01T00:00:00.0000000Z' WHERE Id = $id;",
            ("$id", f.Operation.ToString("N")));

        LongRunningOperationReconciler reconciler = new(f.Operations, [new CancelAfterCompletedHandler(f)],
            TimeProvider.System, NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        await reconciler.ReconcileNowAsync("completion-cancellation-fixture-owner");

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

    private sealed class CancelAfterCompletedHandler(LongRestMaintenanceTests.MaintenanceFixture fixture) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => LongRunningOperationKinds.LongRestSweep;

        public int SupportedCheckpointVersion => 1;

        public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
        {
            LongRunningOperationRecoveryResult completed = await fixture.Coordinator.RecoverAsync(operation, cancellationToken);

            Assert.Equal(LongRunningOperationState.Completed, completed.State);

            LongRunningOperation latest = (await fixture.Operations.GetAsync(operation.Id, cancellationToken))!;

            Assert.True(await fixture.Operations.RequestCancellationAsync(operation.Id, latest.Revision, DateTimeOffset.UtcNow, cancellationToken));

            return completed;
        }
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
