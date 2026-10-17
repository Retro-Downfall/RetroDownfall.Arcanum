using System.Reflection;

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
    public void The_handler_is_pure_with_no_dependencies_and_one_recovery_entry()
    {
        Type handler = typeof(LongRestSweepRecoveryHandler);

        // No constructor parameter and no stored member: recovery cannot reach a provider, a store or the coordinator.
        ConstructorInfo constructor = Assert.Single(handler.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));

        Assert.Empty(constructor.GetParameters());

        Assert.Empty(handler.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static));

        Assert.Single(handler.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            static method => method.Name == nameof(ILongRunningOperationRecoveryHandler.RecoverAsync));
    }

    [Fact]
    public async Task Version_zero_recovery_abandons_without_effects()
    {
        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(Operation(), CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Equal("long_rest.not_started", result.ErrorCode);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Running, LongRestParkCodes.Interrupted)]
    [InlineData(LongRunningOperationState.Waiting, LongRestParkCodes.Interrupted)]
    [InlineData(LongRunningOperationState.Pending, LongRestParkCodes.ReadmitRequested)]
    public async Task Version_one_clean_recovery_parks_by_state_and_never_runs_the_sweep(LongRunningOperationState state, string expected)
    {
        LongRunningOperation operation = Clean(state);

        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(expected, result.ErrorCode);

        Assert.Equal(LongRestFaultClass.ResumeLater, LongRestFaultPolicy.Classify(result.ErrorCode));
    }

    [Fact]
    public async Task A_running_row_carrying_the_retry_code_is_an_operator_retry_not_a_crash()
    {
        // Lease acquisition records this code when it adopts a Pending row that was already attempted.
        LongRunningOperation operation = Clean(LongRunningOperationState.Running) with { TerminalErrorCode = LongRestParkCodes.ReadmitRequested };

        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(LongRestParkCodes.ReadmitRequested, result.ErrorCode);
    }

    [Theory]
    [InlineData(LongRunningOperationState.Cancelling, 0)]
    [InlineData(LongRunningOperationState.Cancelling, 1)]
    public async Task Cancelling_recovery_abandons_as_cancelled(LongRunningOperationState state, int version)
    {
        LongRunningOperation operation = (version == 1 ? Clean(state) : Operation()) with { State = state };

        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Equal("long_rest.cancelled", result.ErrorCode);
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

        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal("long_rest.authorized_request_required", result.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public async Task A_corrupt_checkpoint_parks_under_the_codec_code_for_an_operator(string? payload)
    {
        LongRunningOperation operation = Operation() with
        {
            CheckpointVersion = 1,
            CheckpointPayload = payload is null ? null : System.Text.Encoding.UTF8.GetBytes(payload),
        };

        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(operation, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(LongRestParkCodes.CheckpointInvalid, result.ErrorCode);

        Assert.Equal(LongRestFaultClass.NeedsOperator, LongRestFaultPolicy.Classify(result.ErrorCode));
    }

    [Fact]
    public async Task A_checkpoint_version_outside_the_window_is_unsupported()
    {
        var result = await new LongRestSweepRecoveryHandler().RecoverAsync(Operation() with { CheckpointVersion = 2 }, CancellationToken.None);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(LongRunningOperationErrorCodes.UnsupportedCheckpointVersion, result.ErrorCode);
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

        // Recovery only parks, so neither checkpoint version ever needs an external-effect group.
        Assert.Equal(LongRunningRecoveryAdmissionKind.OrdinaryDbOnly, LongRunningOperationRecoveryAdmission.Classify(Operation(), null).Kind);

        Assert.Equal(LongRunningRecoveryAdmissionKind.OrdinaryDbOnly, LongRunningOperationRecoveryAdmission.Classify(Operation() with { CheckpointVersion = 1 }, null).Kind);

        Assert.DoesNotContain("OrdinaryPerUnitExternalEffect", Enum.GetNames<LongRunningRecoveryAdmissionKind>());
    }

    private static LongRunningOperation Operation() => new FakeLongRunningOperationStore(TimeProvider.System)
        .Seed(LongRunningOperationKinds.LongRestSweep, LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint);

    private static LongRunningOperation Clean(LongRunningOperationState state)
    {
        LongRunningOperation operation = Operation();

        LongRestSweepCheckpoint checkpoint = new(1, operation.Id, LongRestSweepScopeKind.Global, null, null, null, 0, null, null, null);

        return operation with { State = state, CheckpointVersion = 1, CheckpointPayload = LongRestSweepCheckpointCodec.Encode(checkpoint).Value };
    }
}
