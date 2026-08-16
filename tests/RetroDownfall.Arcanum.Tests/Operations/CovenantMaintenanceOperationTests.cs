using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Covenant;

namespace RetroDownfall.Arcanum.Tests.Operations;

/// <summary>
/// The two durable Covenant operation kinds: their descriptors, checkpoints, and handlers.
/// </summary>
public sealed class CovenantMaintenanceOperationTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Theory]
    [InlineData("covenant-index-rebuild")]
    [InlineData("covenant-family-reinitialize")]
    public void Covenant_operation_kinds_have_exactly_one_descriptor_each(string kind)
    {

        LongRunningOperationRecoveryDescriptor? descriptor = LongRunningOperationRecoveryRegistry.Find(kind);

        Assert.NotNull(descriptor);

        Assert.Equal(LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint, descriptor.Policy);

        Assert.Equal(1, descriptor.MinCheckpointVersion);

        Assert.Equal(1, descriptor.MaxCheckpointVersion);

        Assert.Single(
            LongRunningOperationRecoveryRegistry.Descriptors.Values.Where(entry => entry.Kind == kind));

    }

    [Fact]
    public void The_kind_literals_are_pinned_and_the_reinitialize_recovers_before_state_writes()
    {

        Assert.Equal("covenant-index-rebuild", LongRunningOperationKinds.CovenantIndexRebuild);

        Assert.Equal("covenant-family-reinitialize", LongRunningOperationKinds.CovenantFamilyReinitialize);

        Assert.Equal(
            LongRunningOperationStartupPriority.BeforeStateWrites,
            LongRunningOperationRecoveryRegistry
                .Find(LongRunningOperationKinds.CovenantFamilyReinitialize)!
                .StartupPriority);

        Assert.Equal(
            LongRunningOperationStartupPriority.Readiness,
            LongRunningOperationRecoveryRegistry
                .Find(LongRunningOperationKinds.CovenantIndexRebuild)!
                .StartupPriority);

    }

    [Fact]
    public void Index_rebuild_checkpoints_round_trip_the_exact_rebuilder_cursor()
    {

        CovenantIndexRebuildProgress progress = new(
            CovenantOperationGateFixture.DatasetGeneration,
            AcceleratorEpoch: 4,
            BaseTargetSearchSequence: 120,
            CapturedCoreCampaignDeletionSequence: 3,
            CovenantIndexRebuildPhase.DeltaCatchUp,
            BaseScanAfterSearchRowId: 17,
            LastContiguousAppliedSequence: 90,
            BaseHeadsProcessed: 256,
            BaseHeadsTotal: 1024,
            DeltaRowsProcessed: 12);

        CovenantIndexRebuildCheckpointV1 checkpoint = CovenantIndexRebuildCoordinator.ToCheckpoint(progress);

        LongRunningOperation operation = Operation(
            LongRunningOperationKinds.CovenantIndexRebuild,
            CovenantRecoveryCheckpointCodec.Encode(checkpoint));

        Result<CovenantIndexRebuildProgress?> decoded =
            CovenantIndexRebuildCoordinator.DecodeCheckpoint(operation);

        Assert.True(decoded.IsSuccess);

        Assert.Equal(progress, decoded.Value);

        Assert.Equal(1, checkpoint.Version);

    }

    [Fact]
    public void A_rebuild_operation_without_a_checkpoint_decodes_as_a_fresh_cursor()
    {

        Result<CovenantIndexRebuildProgress?> decoded = CovenantIndexRebuildCoordinator.DecodeCheckpoint(
            Operation(LongRunningOperationKinds.CovenantIndexRebuild, checkpoint: null));

        Assert.True(decoded.IsSuccess);

        Assert.Null(decoded.Value);

    }

    [Fact]
    public async Task Rebuild_recovery_completes_a_verified_index_and_restarts_a_stale_one()
    {

        CovenantIndexRebuildRecoveryHandler handler = new();

        Assert.Equal(LongRunningOperationKinds.CovenantIndexRebuild, handler.Kind);

        Assert.Equal(1, handler.SupportedCheckpointVersion);

        LongRunningOperationRecoveryResult completed = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.CovenantIndexRebuild,
                CovenantRecoveryCheckpointCodec.Encode(Checkpoint(CovenantIndexRebuildPhase.Completed))),
            Token);

        Assert.Equal(LongRunningOperationState.Completed, completed.State);

        LongRunningOperationRecoveryResult stale = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.CovenantIndexRebuild,
                CovenantRecoveryCheckpointCodec.Encode(Checkpoint(CovenantIndexRebuildPhase.BaseScan))),
            Token);

        Assert.Equal(LongRunningOperationState.Abandoned, stale.State);

        Assert.Equal(
            CovenantIndexRebuildCoordinator.CovenantIndexRebuildRestartCode,
            stale.ErrorCode);

    }

    [Fact]
    public async Task Rebuild_recovery_never_reports_an_unreadable_checkpoint_as_success()
    {

        LongRunningOperationRecoveryResult result = await new CovenantIndexRebuildRecoveryHandler().RecoverAsync(
            Operation(LongRunningOperationKinds.CovenantIndexRebuild, [0x7B, 0x7D]),
            Token);

        Assert.Equal(LongRunningOperationState.Abandoned, result.State);

        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, result.ErrorCode);

    }

    [Theory]
    [InlineData(CovenantFamilyReinitializePhase.Planned)]
    [InlineData(CovenantFamilyReinitializePhase.AdmissionClosed)]
    [InlineData(CovenantFamilyReinitializePhase.LocalArtifactsProcessed)]
    [InlineData(CovenantFamilyReinitializePhase.HandlesClosed)]
    [InlineData(CovenantFamilyReinitializePhase.FamilyDropped)]
    [InlineData(CovenantFamilyReinitializePhase.DatabaseCompacted)]
    [InlineData(CovenantFamilyReinitializePhase.CanonicalInstalled)]
    [InlineData(CovenantFamilyReinitializePhase.AcceleratorInstalled)]
    [InlineData(CovenantFamilyReinitializePhase.FinalWalTruncated)]
    [InlineData(CovenantFamilyReinitializePhase.SidecarsVerified)]
    public async Task Reinitialize_recovery_parks_every_nonterminal_phase_for_resume(
        CovenantFamilyReinitializePhase phase)
    {

        LongRunningOperationRecoveryResult result = await new CovenantFamilyReinitializeRecoveryHandler()
            .RecoverAsync(
                Operation(
                    LongRunningOperationKinds.CovenantFamilyReinitialize,
                    CovenantRecoveryCheckpointCodec.Encode(ReinitializeCheckpoint(phase))),
                Token);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(
            CovenantFamilyReinitializeRecoveryHandler.CovenantReinitializeResumeRequired,
            result.ErrorCode);

    }

    [Fact]
    public async Task Reinitialize_recovery_completes_only_a_verified_reopen_and_closes_an_unstarted_row()
    {

        CovenantFamilyReinitializeRecoveryHandler handler = new();

        LongRunningOperationRecoveryResult verified = await handler.RecoverAsync(
            Operation(
                LongRunningOperationKinds.CovenantFamilyReinitialize,
                CovenantRecoveryCheckpointCodec.Encode(
                    ReinitializeCheckpoint(CovenantFamilyReinitializePhase.ReopenedVerified))),
            Token);

        Assert.Equal(LongRunningOperationState.Completed, verified.State);

        LongRunningOperationRecoveryResult unstarted = await handler.RecoverAsync(
            Operation(LongRunningOperationKinds.CovenantFamilyReinitialize, checkpoint: null),
            Token);

        Assert.Equal(LongRunningOperationState.Abandoned, unstarted.State);

        Assert.Equal(
            CovenantFamilyReinitializeRecoveryHandler.CovenantReinitializeNeverStarted,
            unstarted.ErrorCode);

    }

    [Fact]
    public async Task Reinitialize_recovery_requires_attention_for_an_unreadable_checkpoint()
    {

        LongRunningOperationRecoveryResult result = await new CovenantFamilyReinitializeRecoveryHandler()
            .RecoverAsync(
                Operation(LongRunningOperationKinds.CovenantFamilyReinitialize, [0x7B, 0x7D]),
                Token);

        Assert.Equal(LongRunningOperationState.ReconciliationRequired, result.State);

        Assert.Equal(LongRunningOperationErrorCodes.CorruptCheckpoint, result.ErrorCode);

    }

    private static CovenantIndexRebuildCheckpointV1 Checkpoint(CovenantIndexRebuildPhase phase) =>
        new(
            CovenantIndexRebuildCheckpointV1.CurrentVersion,
            CovenantOperationGateFixture.DatasetGeneration,
            AcceleratorEpoch: 1,
            BaseTargetSearchSequence: 1,
            CapturedCoreCampaignDeletionSequence: 0,
            phase,
            BaseScanAfterSearchRowId: null,
            LastContiguousAppliedSequence: 0,
            BaseHeadsProcessed: 0,
            BaseHeadsTotal: null,
            DeltaRowsProcessed: 0);

    private static CovenantFamilyReinitializeCheckpointV1 ReinitializeCheckpoint(
        CovenantFamilyReinitializePhase phase) =>
        new(
            CovenantFamilyReinitializeCheckpointV1.CurrentVersion,
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            "6F1C0B2E-9A44-4E1D-8B7A-2C5D3F6A8E90",
            AuthorityEpoch: 1,
            CovenantOperationGateFixture.Digest(1).ToString(),
            CovenantOperationGateFixture.Digest(2).ToString(),
            CovenantOperationGateFixture.Digest(3).ToString(),
            CovenantOperationGateFixture.DatasetGeneration,
            NewDatasetGeneration: null,
            phase,
            ManagedArtifactCursor: 0,
            OldFamilyDropped: false,
            CanonicalInstalled: false,
            AcceleratorInstalled: false,
            CompactedFileIdentityDigest: null,
            RetryCount: 0,
            LastDurableErrorCode: null);

    private static LongRunningOperation Operation(string kind, byte[]? checkpoint) =>
        new(
            Guid.Parse("55555555-5555-4555-8555-555555555555"),
            kind,
            LongRunningOperationState.Running,
            LongRunningOperationRecoveryPolicy.ResumeFromCheckpoint,
            RootOperationId: null,
            ParentOperationId: null,
            SessionId: null,
            RunId: null,
            InferenceRunId: null,
            BudgetReservationId: null,
            IdempotencyClaimId: null,
            DateTimeOffset.UnixEpoch,
            StartedAt: null,
            HeartbeatAt: null,
            CompletedAt: null,
            LeaseOwner: null,
            LeaseExpiresAt: null,
            AttemptCount: 1,
            CheckpointVersion: checkpoint is null ? 0 : 1,
            checkpoint,
            CheckpointReference: null,
            "maintenance",
            TerminalErrorCode: null,
            Revision: 1);

}
