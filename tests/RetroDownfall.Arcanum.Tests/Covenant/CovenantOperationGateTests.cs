using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Covenant;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Lifecycle, coverage, and recovery behaviour of the generation-bound Covenant operation gate.
/// </summary>
public sealed class CovenantOperationGateTests
{

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public void Exclusive_operation_codes_are_immutable()
    {

        Assert.Equal((byte)1, (byte)CovenantExclusiveOperation.CampaignPathMutation);

        Assert.Equal((byte)2, (byte)CovenantExclusiveOperation.CampaignDelete);

        Assert.Equal((byte)3, (byte)CovenantExclusiveOperation.ProtectedSessionTransfer);

        Assert.Equal((byte)4, (byte)CovenantExclusiveOperation.SchemaRepair);

        Assert.Equal((byte)5, (byte)CovenantExclusiveOperation.BackupRestore);

        Assert.Equal((byte)6, (byte)CovenantExclusiveOperation.CovenantFamilyReinitialize);

        Assert.Equal((byte)7, (byte)CovenantExclusiveOperation.CovenantReset);

        Assert.Equal((byte)8, (byte)CovenantExclusiveOperation.HealthyCatalogFactoryErasure);

        Assert.Equal(8, Enum.GetValues<CovenantExclusiveOperation>().Length);

    }

    [Fact]
    public void Lease_disposition_codes_are_immutable()
    {

        Assert.Equal((byte)1, (byte)CovenantExclusiveLeaseDisposition.RollbackAndReopen);

        Assert.Equal((byte)2, (byte)CovenantExclusiveLeaseDisposition.CommitAndReopen);

        Assert.Equal((byte)3, (byte)CovenantExclusiveLeaseDisposition.KeepClosed);

        Assert.Equal(3, Enum.GetValues<CovenantExclusiveLeaseDisposition>().Length);

    }

    [Fact]
    public void Operation_scope_truth_table_is_closed()
    {

        CovenantOperationScope global = CovenantOperationScope.Global;

        Assert.Equal(CovenantScope.Global, global.Kind);

        Assert.Null(global.CampaignId);

        CovenantOperationScope campaign = CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne);

        Assert.Equal(CovenantScope.Campaign, campaign.Kind);

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, campaign.CampaignId);

        _ = Assert.Throws<ArgumentException>(() => CovenantOperationScope.ForCampaign(Guid.Empty));

        _ = Assert.Throws<InvalidOperationException>(() => default(CovenantOperationScope).Kind);

    }

    [Fact]
    public void Protected_transfer_scope_truth_table_is_closed()
    {

        ProtectedTransferScope global = ProtectedTransferScope.Global;

        Assert.Equal(CovenantScope.Global, global.Kind);

        Assert.Null(global.CampaignId);

        ProtectedTransferScope campaign = ProtectedTransferScope.ForCampaign(CovenantOperationGateFixture.CampaignOne);

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, campaign.CampaignId);

        _ = Assert.Throws<ArgumentException>(() => ProtectedTransferScope.ForCampaign(Guid.Empty));

        _ = Assert.Throws<InvalidOperationException>(() => default(ProtectedTransferScope).Kind);

    }

    [Fact]
    public void Recovery_owner_requires_identity_operation_and_effect()
    {

        _ = Assert.Throws<ArgumentException>(
            () => new CovenantExclusiveRecoveryOwner(
                Guid.Empty,
                CovenantExclusiveOperation.CovenantReset,
                CovenantOperationGateFixture.Digest(1)));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                (CovenantExclusiveOperation)9,
                CovenantOperationGateFixture.Digest(1)));

        _ = Assert.Throws<ArgumentException>(
            () => new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                CovenantExclusiveOperation.CovenantReset,
                default));

    }

    [Fact]
    public async Task Installation_read_is_the_sole_all_scopes_capability()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantInstallationReadLease> acquired = await gate.AcquireInstallationReadAsync(Token);

        Assert.True(acquired.IsSuccess);

        await using CovenantInstallationReadLease lease = acquired.Value;

        Assert.Equal(CovenantLeaseCoverage.Installation, lease.Snapshot.Coverage);

        Assert.Null(lease.Snapshot.Scope);

        Assert.Equal(CovenantLeaseKind.InstallationRead, lease.Snapshot.Kind);

        Assert.IsAssignableFrom<ICovenantSnapshotReadLease>(lease);

    }

    [Fact]
    public async Task Scoped_leases_bind_their_scope_and_generations()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantReadLease read =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantLeaseCoverage.Scoped, read.Snapshot.Coverage);

        Assert.Equal(CovenantScope.Global, read.Snapshot.Scope!.Value.Kind);

        Assert.Equal(CovenantOperationGateFixture.DatasetGeneration, read.Snapshot.DatasetGeneration);

        Assert.Equal(1, read.Snapshot.AuthorityEpoch);

        await using CovenantWriteLease write = (await gate.AcquireWriteAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.Write, write.Snapshot.Kind);

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, write.Snapshot.Scope!.Value.CampaignId);

        Assert.IsNotAssignableFrom<ICovenantSnapshotReadLease>(write);

    }

    [Fact]
    public async Task Turn_lease_carries_campaign_availability_and_path_revision()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantTurnLease turn = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.Turn, turn.Snapshot.Kind);

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, turn.Snapshot.Scope!.Value.CampaignId);

        Assert.Equal(5, turn.Snapshot.CampaignAvailabilityGeneration);

        Assert.Equal(9, turn.Snapshot.CampaignPathRevision);

        await using CovenantTurnLease globalTurn =
            (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, Token)).Value;

        Assert.Equal(CovenantScope.Global, globalTurn.Snapshot.Scope!.Value.Kind);

        Assert.Null(globalTurn.Snapshot.CampaignAvailabilityGeneration);

    }

    [Fact]
    public async Task Accelerator_lease_binds_epoch_and_applied_deletion_sequence()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantAcceleratorLease accelerator = (await gate.AcquireAcceleratorAsync(Token)).Value;

        Assert.Equal(4UL, accelerator.Snapshot.AcceleratorEpoch);

        Assert.Equal(3, accelerator.Snapshot.AppliedCampaignDeletionSequence);

        Assert.Equal(CovenantLeaseCoverage.Installation, accelerator.Snapshot.Coverage);

    }

    [Fact]
    public async Task Exclusive_operation_codes_are_bound_to_their_acquisition_shape()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantExclusiveLease> campaignCodeOnGlobal = await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token);

        Assert.Equal("Covenant.ForbiddenAuthority", campaignCodeOnGlobal.Error.Code);

        Result<CovenantCampaignExclusiveLease> resetCodeOnCampaign = await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token);

        Assert.Equal("Covenant.ForbiddenAuthority", resetCodeOnCampaign.Error.Code);

        Result<CovenantProtectedTransferLease> wrongTransferCode = await gate.AcquireProtectedTransferAsync(
            ProtectedTransferScope.Global,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token);

        Assert.Equal("Covenant.ForbiddenAuthority", wrongTransferCode.Error.Code);

    }

    [Fact]
    public async Task Initial_campaign_exclusive_refuses_a_missing_campaign()
    {

        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(CovenantOperationGateFixture.CampaignOne, CovenantCampaignScopeState.Deleted);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        Result<CovenantCampaignExclusiveLease> acquired = await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token);

        Assert.Equal("Covenant.NotFound", acquired.Error.Code);

    }

    [Fact]
    public async Task Campaign_exclusive_closes_only_its_own_campaign()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantCampaignExclusiveLease exclusive = (await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token)).Value;

        Result<CovenantReadLease> blocked = await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            Token);

        Assert.Equal("Covenant.Unavailable", blocked.Error.Code);

        await using CovenantReadLease otherCampaign = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        await using CovenantReadLease globalScope =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, globalScope.Snapshot.Scope!.Value.Kind);

        Result<CovenantInstallationReadLease> installation = await gate.AcquireInstallationReadAsync(Token);

        Assert.Equal("Covenant.Unavailable", installation.Error.Code);

    }

    [Fact]
    public async Task Global_exclusive_closes_every_scope()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
                Token)).Error.Code);

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireInstallationReadAsync(Token)).Error.Code);

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireAcceleratorAsync(Token)).Error.Code);

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, Token)).Error.Code);

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireMcpAsync(CovenantOperationScope.Global, Token)).Error.Code);

    }

    [Fact]
    public async Task Closing_a_scope_revokes_and_drains_its_live_leases()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease reader = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        CovenantInstallationReadLease installation = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Task<Result<CovenantCampaignExclusiveLease>> close = gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token).AsTask();

        // The close cannot complete while either affected registration is still live, and both are
        // told to stop rather than being waited out silently.
        await WaitForAsync(() => reader.Revocation.IsCancellationRequested, Token);

        await WaitForAsync(() => installation.Revocation.IsCancellationRequested, Token);

        Assert.False(close.IsCompleted);

        await reader.DisposeAsync();

        await installation.DisposeAsync();

        Result<CovenantCampaignExclusiveLease> exclusive = await close;

        Assert.True(exclusive.IsSuccess);

        await using CovenantCampaignExclusiveLease held = exclusive.Value;

        Assert.Equal(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            held.Snapshot.RecoveryOwner);

    }

    [Fact]
    public async Task A_drain_that_cannot_finish_changes_nothing()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromMilliseconds(150));

        await using CovenantReadLease reader =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantExclusiveLease> exclusive = await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token);

        Assert.Equal("Covenant.MaintenanceFailed", exclusive.Error.Code);

        // Admission reopened: the refused close left no owner behind.
        await using CovenantReadLease afterwards =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, afterwards.Snapshot.Scope!.Value.Kind);

    }

    [Fact]
    public async Task Commit_and_reopen_clears_the_recovery_owner()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, Token)).IsSuccess);

        await exclusive.DisposeAsync();

        await using CovenantReadLease reopened =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, reopened.Snapshot.Scope!.Value.Kind);

        Result<CovenantExclusiveLease> resumed = await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token);

        Assert.Equal("Covenant.ManualRecoveryRequired", resumed.Error.Code);

    }

    [Fact]
    public async Task Rollback_and_reopen_reopens_admission()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantCampaignExclusiveLease exclusive = (await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignPathMutation),
            Token)).Value;

        Assert.True(
            (await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, Token)).IsSuccess);

        await exclusive.DisposeAsync();

        await using CovenantReadLease reopened = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, reopened.Snapshot.Scope!.Value.CampaignId);

    }

    [Fact]
    public async Task Keep_closed_leaves_admission_closed_and_retains_the_owner()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);

        await exclusive.DisposeAsync();

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

        // An ordinary acquisition can never take over a kept-closed owner.
        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireExclusiveAsync(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
                Token)).Error.Code);

        await using CovenantExclusiveLease resumed = (await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Assert.Equal(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            resumed.Snapshot.RecoveryOwner);

    }

    [Fact]
    public async Task Disposition_is_one_shot()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.BackupRestore),
            Token)).Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);

        Result second = await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, Token);

        Assert.Equal("Covenant.LifecycleConflict", second.Error.Code);

    }

    [Fact]
    public async Task Disposing_before_a_disposition_keeps_the_scope_closed()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        await exclusive.DisposeAsync();

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

        await using CovenantExclusiveLease resumed = (await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantFamilyReinitialize),
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.Exclusive, resumed.Snapshot.Kind);

    }

    [Fact]
    public async Task Resume_refuses_a_wrong_identity_effect_kind_or_scope()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantCampaignExclusiveLease exclusive = (await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token)).Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);

        await exclusive.DisposeAsync();

        Assert.Equal(
            "Covenant.ManualRecoveryRequired",
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(
                    CovenantExclusiveOperation.CampaignDelete,
                    operationId: new Guid("55555555-5555-4555-8555-555555555555")),
                Token)).Error.Code);

        Assert.Equal(
            "Covenant.ManualRecoveryRequired",
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete, effectSeed: 99),
                Token)).Error.Code);

        Assert.Equal(
            "Covenant.ForbiddenAuthority",
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
                Token)).Error.Code);

        Assert.Equal(
            "Covenant.ManualRecoveryRequired",
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignTwo,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
                Token)).Error.Code);

    }

    [Fact]
    public async Task Duplicate_live_recovery_is_refused()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);

        await exclusive.DisposeAsync();

        await using CovenantExclusiveLease first = (await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Result<CovenantExclusiveLease> second = await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token);

        Assert.Equal("Covenant.LifecycleConflict", second.Error.Code);

    }

    [Fact]
    public async Task Pre_readiness_recovery_adopts_a_validated_durable_owner()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        gate.AdoptDurableRecoveryOwner(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            scope: null,
            cleanupOnlyHistoricalCampaign: false);

        await using CovenantExclusiveLease resumed = (await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.Exclusive, resumed.Snapshot.Kind);

    }

    [Fact]
    public async Task Post_readiness_recovery_without_a_closed_owner_is_refused()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        gate.PublishReadiness();

        _ = Assert.Throws<InvalidOperationException>(
            () => gate.AdoptDurableRecoveryOwner(
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
                scope: null,
                cleanupOnlyHistoricalCampaign: false));

        Result<CovenantExclusiveLease> resumed = await gate.ResumeExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token);

        Assert.Equal("Covenant.ManualRecoveryRequired", resumed.Error.Code);

    }

    [Fact]
    public async Task A_historical_campaign_resumes_only_from_its_journal()
    {

        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(CovenantOperationGateFixture.CampaignOne, CovenantCampaignScopeState.Deleted);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        Result<CovenantCampaignExclusiveLease> unjournaled = await gate.ResumeCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token);

        Assert.Equal("Covenant.ManualRecoveryRequired", unjournaled.Error.Code);

        gate.AdoptDurableRecoveryOwner(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            cleanupOnlyHistoricalCampaign: true);

        await using CovenantCampaignExclusiveLease resumed = (await gate.ResumeCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token)).Value;

        Assert.True(resumed.Snapshot.CleanupOnlyHistoricalCampaign);

    }

    [Fact]
    public async Task A_finalizer_runs_only_after_a_successful_disposition()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        RecordingPostDispositionFinalizer finalizer = new();

        Assert.True((await exclusive.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            finalizer,
            Token)).IsSuccess);

        Assert.Equal(1, finalizer.Invocations);

        Assert.Equal(CovenantExclusiveLeaseDisposition.CommitAndReopen, finalizer.ObservedDisposition);

    }

    [Fact]
    public async Task A_failed_disposition_skips_the_finalizer()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        Assert.True((await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);

        RecordingPostDispositionFinalizer finalizer = new();

        Result second = await exclusive.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            finalizer,
            Token);

        Assert.True(second.IsFailure);

        Assert.Equal(0, finalizer.Invocations);

    }

    [Fact]
    public async Task A_finalizer_failure_cannot_request_a_second_disposition()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token)).Value;

        RecordingPostDispositionFinalizer finalizer = new(succeed: false);

        Result outcome = await exclusive.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            finalizer,
            Token);

        Assert.Equal("Covenant.MaintenanceFailed", outcome.Error.Code);

        Assert.Equal(1, finalizer.Invocations);

        Result retry = await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, Token);

        Assert.Equal("Covenant.LifecycleConflict", retry.Error.Code);

    }

    [Fact]
    public void The_no_op_finalizer_is_a_sealed_singleton()
    {

        Assert.True(typeof(CovenantNoOpPostDispositionFinalizer).IsSealed);

        Assert.Same(CovenantNoOpPostDispositionFinalizer.Instance, CovenantNoOpPostDispositionFinalizer.Instance);

        Assert.Empty(
            typeof(CovenantNoOpPostDispositionFinalizer).GetConstructors());

    }

    [Fact]
    public async Task Protected_transfer_is_one_compound_snapshot_and_exclusive_lease()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantProtectedTransferLease transfer = (await gate.AcquireProtectedTransferAsync(
            ProtectedTransferScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.ProtectedSessionTransfer),
            Token)).Value;

        Assert.IsAssignableFrom<ICovenantSnapshotReadLease>(transfer);

        Assert.IsAssignableFrom<ICovenantExclusiveOperationLease>(transfer);

        Assert.Equal(CovenantLeaseKind.ProtectedTransfer, transfer.Snapshot.Kind);

        // No second read lease may be combined with the compound lease: its own scope is closed.
        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                Token)).Error.Code);

        Assert.True((await transfer.RevalidateAsync(Token)).IsSuccess);

    }

    [Fact]
    public async Task Revalidation_notices_a_dataset_generation_change()
    {

        FakeCovenantAvailability availability = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantReadLease reader =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.True((await reader.RevalidateAsync(Token)).IsSuccess);

        availability.Mutate(current => current with { DatasetGeneration = Guid.NewGuid() });

        Assert.Equal("Covenant.StaleSnapshot", (await reader.RevalidateAsync(Token)).Error.Code);

    }

    [Fact]
    public async Task Revalidation_notices_an_authority_epoch_change()
    {

        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantTurnLease turn =
            (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, Token)).Value;

        authority.Advance();

        Assert.Equal("Covenant.ForbiddenAuthority", (await turn.RevalidateAsync(Token)).Error.Code);

    }

    [Fact]
    public async Task Revalidation_notices_an_accelerator_epoch_change()
    {

        FakeCovenantAvailability availability = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantAcceleratorLease accelerator = (await gate.AcquireAcceleratorAsync(Token)).Value;

        availability.Mutate(current => current with { AcceleratorEpoch = current.AcceleratorEpoch + 1 });

        Assert.Equal("Covenant.StaleSnapshot", (await accelerator.RevalidateAsync(Token)).Error.Code);

    }

    [Fact]
    public async Task A_disposed_lease_cannot_be_used_late()
    {

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease reader = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        await reader.DisposeAsync();

        Assert.Equal("Covenant.StaleSnapshot", (await reader.RevalidateAsync(Token)).Error.Code);

        // Repeated disposal is a no-op rather than a second release of a slot another lease may own.
        await reader.DisposeAsync();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.Exclusive, exclusive.Snapshot.Kind);

    }

    [Fact]
    public async Task Acquisition_fails_when_authority_is_not_established()
    {

        FakeCovenantAuthorityProvider authority = new();

        authority.Clear();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        Assert.Equal(
            "Covenant.OperatorAuthorityUnavailable",
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

    }

    [Fact]
    public async Task Acquisition_fails_when_the_canonical_tier_is_unusable()
    {

        FakeCovenantAvailability availability = new();

        availability.Mutate(current => current with
        {

            Canonical = CovenantCapabilityState.Unavailable,

            DatasetGeneration = null,

        });

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        Assert.Equal(
            "Covenant.Unavailable",
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {

        for (int attempt = 0; attempt < 500; attempt++)
        {

            if (condition())
            {

                return;

            }

            await Task.Delay(10, cancellationToken);

        }

        Assert.Fail("The awaited gate condition never became true.");

    }

}
