using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Operations.LongRest;
using RetroDownfall.Arcanum.Tests.Operations;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestSweepRecoveryTests
{
    [Fact]
    public async Task Actual_reconciler_honors_cancellation_arriving_after_the_LongRest_handler_result()
    {
        await using var f = await LongRestMaintenanceTests.MaintenanceFixture.CreateAsync(ownWorker: false);

        CancellingHandler handler = new(f.Operations);

        LongRunningOperationReconciler reconciler = new(f.Operations, [handler], TimeProvider.System,
            NullLogger<LongRunningOperationReconciler>.Instance, new LongRunningOperationOwnership());

        await reconciler.ReconcileNowAsync("fixture-recovery-owner");

        Assert.Equal(1, handler.Calls);

        var operation = (await f.Operations.GetAsync(f.Operation))!;

        Assert.Equal(LongRunningOperationState.Abandoned, operation.State);

        Assert.Equal("long_rest.cancelled", operation.TerminalErrorCode);
    }

    private sealed class CancellingHandler(ILongRunningOperationStore operations) : ILongRunningOperationRecoveryHandler
    {
        public string Kind => LongRunningOperationKinds.LongRestSweep;

        public int SupportedCheckpointVersion => 1;

        internal int Calls { get; private set; }

        public async Task<LongRunningOperationRecoveryResult> RecoverAsync(LongRunningOperation operation, CancellationToken cancellationToken)
        {
            Calls++;

            Assert.True(await operations.RequestCancellationAsync(operation.Id, operation.Revision, DateTimeOffset.UtcNow, cancellationToken));

            return LongRunningOperationRecoveryResult.Completed();
        }
    }

    [Fact]
    public async Task Version_zero_recovery_abandons_without_effects()
    {
        LongRunningOperation operation = Operation();

        LongRestSweepRecoveryHandler handler = new(null!);

        var result = await handler.RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Equal("long_rest.not_started", result.ErrorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_recovery_never_reconstructs_authority(bool campaign)
    {
        LongRunningOperation operation = Operation();

        LongRestSweepCheckpoint checkpoint = new(1, operation.Id, campaign ? LongRestSweepScopeKind.Campaign : LongRestSweepScopeKind.Global,
            campaign ? Guid.NewGuid() : null, null, null, 0, null, null, new CovenantDigest(new byte[32]));

        operation = operation with { CheckpointVersion = 1, CheckpointPayload = LongRestSweepCheckpointCodec.Encode(checkpoint).Value };

        var result = await new LongRestSweepRecoveryHandler(null!).RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal("long_rest.authorized_request_required", result.ErrorCode);
    }

    [Fact]
    public void Registry_and_closed_admission_classify_the_actual_new_kind()
    {
        var descriptor = LongRunningOperationRecoveryRegistry.Find(LongRunningOperationKinds.LongRestSweep);

        Assert.NotNull(descriptor);

        Assert.Equal("LongRestSweepCoordinator", descriptor.Owner);

        Assert.Equal(LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint, descriptor.Policy);

        Assert.Equal(0, descriptor.MinCheckpointVersion);

        Assert.Equal(1, descriptor.MaxCheckpointVersion);

        Assert.Equal(LongRunningRecoveryAdmissionKind.OrdinaryDbOnly, LongRunningOperationRecoveryAdmission.Classify(Operation(), null).Kind);

        Assert.Equal(LongRunningRecoveryAdmissionKind.OrdinaryPerUnitExternalEffect, LongRunningOperationRecoveryAdmission.Classify(Operation() with { CheckpointVersion = 1 }, null).Kind);
    }

    private static LongRunningOperation Operation() => new FakeLongRunningOperationStore(TimeProvider.System)
        .Seed(LongRunningOperationKinds.LongRestSweep, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint);
}
