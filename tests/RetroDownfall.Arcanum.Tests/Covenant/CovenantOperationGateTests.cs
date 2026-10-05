using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
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

        Assert.Equal((byte)9, (byte)CovenantExclusiveOperation.CovenantEntryErasure);

        Assert.Equal(9, Enum.GetValues<CovenantExclusiveOperation>().Length);
    }

    [Fact]
    public void Lease_kind_codes_are_immutable()
    {
        Assert.Equal((byte)1, (byte)CovenantLeaseKind.InstallationRead);

        Assert.Equal((byte)2, (byte)CovenantLeaseKind.Read);

        Assert.Equal((byte)3, (byte)CovenantLeaseKind.Write);

        Assert.Equal((byte)4, (byte)CovenantLeaseKind.Turn);

        Assert.Equal((byte)5, (byte)CovenantLeaseKind.Mcp);

        Assert.Equal((byte)6, (byte)CovenantLeaseKind.Accelerator);

        Assert.Equal((byte)7, (byte)CovenantLeaseKind.Cleanup);

        Assert.Equal((byte)8, (byte)CovenantLeaseKind.CampaignExclusive);

        Assert.Equal((byte)9, (byte)CovenantLeaseKind.ProtectedTransfer);

        Assert.Equal((byte)10, (byte)CovenantLeaseKind.Exclusive);

        Assert.Equal((byte)11, (byte)CovenantLeaseKind.EntryErasure);

        Assert.Equal(11, Enum.GetValues<CovenantLeaseKind>().Length);
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
                (CovenantExclusiveOperation)10,
                CovenantOperationGateFixture.Digest(1)));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                (CovenantExclusiveOperation)0,
                CovenantOperationGateFixture.Digest(1)));

        _ = Assert.Throws<ArgumentException>(
            () => new CovenantExclusiveRecoveryOwner(
                Guid.NewGuid(),
                CovenantExclusiveOperation.CovenantReset,
                default));

        CovenantExclusiveRecoveryOwner entryErasure = new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantEntryErasure,
            CovenantOperationGateFixture.Digest(1));

        Assert.Equal(CovenantExclusiveOperation.CovenantEntryErasure, entryErasure.Operation);

        Assert.True(entryErasure.IsValid);
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

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, campaignCodeOnGlobal.Error.Code);

        Result<CovenantCampaignExclusiveLease> resetCodeOnCampaign = await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, resetCodeOnCampaign.Error.Code);

        Result<CovenantProtectedTransferLease> wrongTransferCode = await gate.AcquireProtectedTransferAsync(
            ProtectedTransferScope.Global,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.SchemaRepair),
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, wrongTransferCode.Error.Code);

        CovenantExclusiveRecoveryOwner entryErasure =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.AcquireExclusiveAsync(entryErasure, Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.ResumeOrAcquireExclusiveAsync(entryErasure, Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.ResumeExclusiveAsync(entryErasure, Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.AcquireCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                entryErasure,
                Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.AcquireProtectedTransferAsync(
                ProtectedTransferScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                entryErasure,
                Token)).Error.Code);

        // Every refusal above happened before a closure was installed, so the installation is open.
        await using CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
    }

    [Theory]
    [InlineData(CovenantExclusiveOperation.CampaignPathMutation)]
    [InlineData(CovenantExclusiveOperation.CampaignDelete)]
    [InlineData(CovenantExclusiveOperation.ProtectedSessionTransfer)]
    [InlineData(CovenantExclusiveOperation.SchemaRepair)]
    [InlineData(CovenantExclusiveOperation.BackupRestore)]
    [InlineData(CovenantExclusiveOperation.CovenantFamilyReinitialize)]
    [InlineData(CovenantExclusiveOperation.CovenantReset)]
    [InlineData(CovenantExclusiveOperation.HealthyCatalogFactoryErasure)]
    public async Task The_entry_erasure_shape_refuses_every_other_operation_code(
        CovenantExclusiveOperation operation)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner = CovenantOperationGateFixture.Owner(operation);

        Result<CovenantEntryErasureLease> campaignSlot = await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            owner,
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, campaignSlot.Error.Code);

        Result<CovenantEntryErasureLease> installationSlot = await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.Global,
            reclaimsKey: true,
            owner,
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, installationSlot.Error.Code);

        await using CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
    }

    /// <summary>
    /// The whole operation enum against every exclusive acquisition, resume, and durable adoption
    /// shape the gate has.
    /// </summary>
    /// <remarks>
    /// Every shape admits an explicit allow-list, so an operation added to the enum later is refused
    /// by all of them until someone decides where it belongs. The table must name every member, which
    /// is what forces that decision to be made here rather than inherited from a default arm.
    /// </remarks>
    [Fact]
    public async Task Every_operation_code_is_admitted_only_by_its_own_shapes()
    {
        string[] campaign = ["campaign-exclusive", "resume-campaign-exclusive", "adopt-scoped"];

        string[] transfer = ["protected-transfer", "resume-protected-transfer", "adopt-scoped"];

        string[] installation = ["exclusive", "resume-or-acquire-exclusive", "resume-exclusive", "adopt-installation"];

        Dictionary<CovenantExclusiveOperation, string[]> expected = new()
        {
            [CovenantExclusiveOperation.CampaignPathMutation] = campaign,

            [CovenantExclusiveOperation.CampaignDelete] = campaign,

            [CovenantExclusiveOperation.ProtectedSessionTransfer] = transfer,

            [CovenantExclusiveOperation.SchemaRepair] = installation,

            [CovenantExclusiveOperation.BackupRestore] = installation,

            [CovenantExclusiveOperation.CovenantFamilyReinitialize] = installation,

            [CovenantExclusiveOperation.CovenantReset] = installation,

            [CovenantExclusiveOperation.HealthyCatalogFactoryErasure] = installation,

            [CovenantExclusiveOperation.CovenantEntryErasure] = ["entry-erasure"],
        };

        Assert.Equal(Enum.GetValues<CovenantExclusiveOperation>(), expected.Keys.Order());

        List<string> admitted = [];

        foreach (CovenantExclusiveOperation operation in Enum.GetValues<CovenantExclusiveOperation>())
        {
            List<string> shapes = [];

            foreach ((string shape, Func<CovenantOperationGate, CovenantExclusiveRecoveryOwner, Task<bool>> admits) in ExclusiveShapes)
            {
                // A fresh gate per attempt: an admitted acquisition installs a closure, and the next
                // shape must be judged on its own allow-list rather than on that closure.
                if (await admits(
                        CovenantOperationGateFixture.CreateGate(),
                        CovenantOperationGateFixture.Owner(operation)))
                {
                    shapes.Add(shape);
                }
            }

            admitted.Add($"{operation}: {string.Join(", ", shapes)}");
        }

        Assert.Equal(
            expected
                .OrderBy(static row => row.Key)
                .Select(static row => $"{row.Key}: {string.Join(", ", row.Value)}"),
            admitted);
    }

    /// <summary>
    /// What a future operation code meets before anyone has decided where it belongs.
    /// </summary>
    /// <remarks>
    /// The recovery owner's constructor refuses a code outside the enum, so the owner is forged
    /// through its backing field to stand in for a member added later. Every acquisition, resume, and
    /// durable adoption must refuse it: a default arm that classified it as installation-wide would
    /// hand an unreviewed operation the powers of a reset.
    /// </remarks>
    [Fact]
    public async Task An_operation_code_no_shape_names_is_refused_by_every_shape()
    {
        CovenantExclusiveRecoveryOwner unnamed = ForgeOperation(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            (CovenantExclusiveOperation)10);

        Assert.True(unnamed.IsValid);

        List<string> admitted = [];

        foreach ((string shape, Func<CovenantOperationGate, CovenantExclusiveRecoveryOwner, Task<bool>> admits) in ExclusiveShapes)
        {
            if (await admits(CovenantOperationGateFixture.CreateGate(), unnamed))
            {
                admitted.Add(shape);
            }
        }

        Assert.Empty(admitted);
    }

    /// <summary>
    /// A Campaign the deletion journal has an event for refuses with a different code than one
    /// that was simply never registered - see
    /// <see cref="Initial_campaign_exclusive_refuses_an_unregistered_campaign"/>.
    /// </summary>
    [Fact]
    public async Task Initial_campaign_exclusive_refuses_a_deleted_campaign()
    {
        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(CovenantOperationGateFixture.CampaignOne, CovenantCampaignScopeState.Deleted);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        Result<CovenantCampaignExclusiveLease> acquired = await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, acquired.Error.Code);
    }

    /// <summary>
    /// The sibling of <see cref="Initial_campaign_exclusive_refuses_a_deleted_campaign"/> - a
    /// Campaign the probe cannot place in the deletion journal either, which may be a typo or a stale
    /// client rather than a real deletion, refuses with the older, less specific code.
    /// </summary>
    [Fact]
    public async Task Initial_campaign_exclusive_refuses_an_unregistered_campaign()
    {
        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(CovenantOperationGateFixture.CampaignOne, CovenantCampaignScopeState.Unknown);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        Result<CovenantCampaignExclusiveLease> acquired = await gate.AcquireCampaignExclusiveAsync(
            CovenantOperationGateFixture.CampaignOne,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
            Token);

        Assert.Equal(ErrorCodes.Covenant.NotFound, acquired.Error.Code);
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

        Assert.Equal(ErrorCodes.Covenant.Unavailable, blocked.Error.Code);

        await using CovenantReadLease otherCampaign = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        await using CovenantReadLease globalScope =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, globalScope.Snapshot.Scope!.Value.Kind);

        Result<CovenantInstallationReadLease> installation = await gate.AcquireInstallationReadAsync(Token);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, installation.Error.Code);
    }

    [Fact]
    public async Task Global_exclusive_closes_every_scope()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
                Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireInstallationReadAsync(Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireAcceleratorAsync(Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
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

    /// <summary>
    /// R-166: cancelling a token source runs consumer callbacks synchronously, and a callback that throws
    /// surfaces as an exception from <c>Cancel()</c>. That used to escape the exclusive acquisition after
    /// its closure was installed, so the scope stayed closed for the life of the process and every later
    /// acquisition answered "another operation already owns this scope". A faulty consumer callback is now
    /// contained: the token is still cancelled, the close carries on and drains, and nothing is left
    /// installed when it completes.
    /// </summary>
    [Fact]
    public async Task Exclusive_acquisition_removes_its_closure_when_a_revocation_callback_throws()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease reader = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        using CancellationTokenRegistration faulty = reader.Revocation.Register(
            static () => throw new InvalidOperationException("a consumer callback that faults"));

        Task<Result<CovenantExclusiveLease>> close = gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token).AsTask();

        await WaitForAsync(() => reader.Revocation.IsCancellationRequested, Token);

        await reader.DisposeAsync();

        Result<CovenantExclusiveLease> exclusive = await close;

        Assert.True(exclusive.IsSuccess, exclusive.IsFailure ? exclusive.Error.Message : string.Empty);

        Assert.True((await exclusive.Value.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, Token)).IsSuccess);

        await exclusive.Value.DisposeAsync();

        // Nothing stayed installed: admission is open and another close can be taken.
        await using CovenantReadLease reopened =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, reopened.Snapshot.Scope!.Value.Kind);
    }

    /// <summary>
    /// R-166: the closure was removed on a revocation or drain failure, but the lock block that builds the
    /// registration after a successful drain was outside that cleanup, so a fault there left the closure
    /// installed for good. DESIGN says a close that fails for any reason after installing its closure
    /// removes it before it answers, which is now true of that window too, for both acquisition paths.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_close_that_faults_after_draining_removes_its_closure_before_it_answers(bool resumeOrAcquire)
    {
        int faults = 0;

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            afterDrain: () =>
            {
                if (faults++ == 0)
                {
                    throw new InvalidOperationException("faulted after the drain");
                }
            });

        CovenantExclusiveRecoveryOwner owner = CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => (resumeOrAcquire
                ? gate.ResumeOrAcquireExclusiveAsync(owner, Token)
                : gate.AcquireExclusiveAsync(owner, Token)).AsTask());

        // Nothing stayed installed: admission is open, and the same close is not refused as owned.
        CovenantReadLease reopened =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, reopened.Snapshot.Scope!.Value.Kind);

        await reopened.DisposeAsync();

        Result<CovenantExclusiveLease> next = await gate.AcquireExclusiveAsync(owner, Token);

        Assert.True(next.IsSuccess, next.IsFailure ? next.Error.Message : string.Empty);

        await next.Value.DisposeAsync();
    }

    /// <summary>
    /// R-169: the caller giving up while a close drains is cancellation, not a maintenance failure. It used
    /// to be folded into the timeout arm and reported as <c>MaintenanceFailed</c>.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_acquire_while_draining_throws_cancellation_rather_than_MaintenanceFailed()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromSeconds(30));

        await using CovenantReadLease reader =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        using CancellationTokenSource caller = new();

        Task<Result<CovenantExclusiveLease>> close = gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            caller.Token).AsTask();

        await WaitForAsync(() => reader.Revocation.IsCancellationRequested, Token);

        await caller.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close);
    }

    /// <summary>
    /// R-169: a cancelled close leaves no closure behind and does not pretend it changed nothing. The
    /// holders it revoked stay revoked, so admission reopens for whoever acquires next.
    /// </summary>
    [Fact]
    public async Task A_cancelled_acquisition_reports_cancellation_and_revoked_holders()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromSeconds(30));

        CovenantReadLease reader = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        using CancellationTokenSource caller = new();

        Task<Result<CovenantExclusiveLease>> close = gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            caller.Token).AsTask();

        await WaitForAsync(() => reader.Revocation.IsCancellationRequested, Token);

        await caller.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close);

        // The work the close revoked was told to stop, and stays stopped.
        Assert.True(reader.Revocation.IsCancellationRequested);

        Assert.False((await reader.RevalidateAsync(Token)).IsSuccess);

        await reader.DisposeAsync();

        // The cancelled close installed nothing that outlives it.
        await using (CovenantReadLease afterwards =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value)
        {
            Assert.Equal(CovenantScope.Global, afterwards.Snapshot.Scope!.Value.Kind);
        }

        await using CovenantExclusiveLease retry = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.Exclusive, retry.Snapshot.Kind);
    }

    [Fact]
    public async Task A_drain_that_cannot_finish_reopens_admission_and_says_the_work_was_cancelled()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromMilliseconds(150));

        await using CovenantReadLease reader =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Result<CovenantExclusiveLease> exclusive = await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, exclusive.Error.Code);

        // R-169: the leases it drained were revoked, so "nothing was changed" would be false. It says what
        // happened to the work, and that the operation itself never started.
        Assert.Contains("cancelled", exclusive.Error.Message, StringComparison.Ordinal);

        Assert.Contains("did not start", exclusive.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("nothing was changed", exclusive.Error.Message, StringComparison.Ordinal);

        Assert.True(reader.Revocation.IsCancellationRequested);

        // Admission reopened: the refused close left no owner behind.
        await using CovenantReadLease afterwards =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, afterwards.Snapshot.Scope!.Value.Kind);
    }

    /// <summary>
    /// R-167: the lease claimed its one disposition and only then asked the gate to complete under the
    /// caller's token, which checks it first. A token already cancelled burned the claim without completing
    /// anything: the scope stayed closed and every later attempt answered "already used its disposition".
    /// The token is now checked before anything is claimed, so cancelling before the start spends nothing.
    /// </summary>
    [Fact]
    public async Task A_completion_cancelled_before_it_starts_leaves_the_disposition_unspent()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        using CancellationTokenSource cancelled = new();

        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, cancelled.Token));

        // Nothing was spent, so the one disposition is still there to be used.
        Result completed = await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, Token);

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : string.Empty);

        await exclusive.DisposeAsync();

        await using CovenantReadLease reopened =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, reopened.Snapshot.Scope!.Value.Kind);
    }

    /// <summary>
    /// R-167: once the disposition is spent, the journal finalizer is the rest of the same decision. It
    /// used to receive the caller's token, so a cancel landing after the gate had reopened made the
    /// finalizer throw and left the journal behind a disposition that had already happened. It now runs on
    /// no token at all.
    /// </summary>
    [Fact]
    public async Task The_post_disposition_finalizer_never_sees_the_callers_token()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        using CancellationTokenSource caller = new();

        TokenRecordingFinalizer finalizer = new();

        Result completed = await exclusive.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            finalizer,
            caller.Token);

        Assert.True(completed.IsSuccess, completed.IsFailure ? completed.Error.Message : string.Empty);

        Assert.True(finalizer.Ran);

        Assert.False(finalizer.ReceivedToken.CanBeCanceled);

        await exclusive.DisposeAsync();
    }

    private sealed class TokenRecordingFinalizer : ICovenantExclusivePostDispositionFinalizer
    {
        internal bool Ran { get; private set; }

        internal CancellationToken ReceivedToken { get; private set; }

        public ValueTask<Result> FinalizeAfterSuccessfulDispositionAsync(
            CovenantExclusiveLeaseDisposition disposition,
            CancellationToken cancellationToken)
        {
            Ran = true;

            ReceivedToken = cancellationToken;

            return ValueTask.FromResult(Result.Success());
        }
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

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, resumed.Error.Code);
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
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);

        // An ordinary acquisition can never take over a kept-closed owner.
        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
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

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);
    }

    [Fact]
    public async Task Final_publication_runs_only_while_the_exact_registration_still_holds_its_closure()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        int publications = 0;

        Result held = exclusive.ExecuteWhileHeld(() =>
        {
            publications++;

            return Result.Success();
        });

        Assert.True(held.IsSuccess);

        Assert.Equal(1, publications);

        Assert.True((await exclusive.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            Token)).IsSuccess);

        Result afterDisposition = exclusive.ExecuteWhileHeld(() =>
        {
            publications++;

            return Result.Success();
        });

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, afterDisposition.Error.Code);

        Assert.Equal(1, publications);

        await exclusive.DisposeAsync();
    }

    [Fact]
    public async Task A_disposed_or_replaced_registration_cannot_publish_through_the_retained_closure()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner = CovenantOperationGateFixture.Owner(
            CovenantExclusiveOperation.SchemaRepair);

        CovenantExclusiveLease original = (await gate.AcquireExclusiveAsync(owner, Token)).Value;

        ICovenantExclusiveLeaseRegistration originalRegistration =
            (ICovenantExclusiveLeaseRegistration)typeof(CovenantExclusiveOperationLease)
                .GetField("_exclusive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(original)!;

        await original.DisposeAsync();

        int publications = 0;

        Result disposed = original.ExecuteWhileHeld(() =>
        {
            publications++;

            return Result.Success();
        });

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, disposed.Error.Code);

        await using CovenantExclusiveLease replacement = (await gate.ResumeExclusiveAsync(owner, Token)).Value;

        Result replaced = originalRegistration.ExecuteWhileHeld(() =>
        {
            publications++;

            return Result.Success();
        });

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, replaced.Error.Code);

        Assert.True(replacement.ExecuteWhileHeld(() =>
        {
            publications++;

            return Result.Success();
        }).IsSuccess);

        Assert.Equal(1, publications);
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
            ErrorCodes.Covenant.Unavailable,
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
            ErrorCodes.Covenant.ManualRecoveryRequired,
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(
                    CovenantExclusiveOperation.CampaignDelete,
                    operationId: new Guid("55555555-5555-4555-8555-555555555555")),
                Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ManualRecoveryRequired,
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete, effectSeed: 99),
                Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.ResumeCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
                Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ManualRecoveryRequired,
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

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, second.Error.Code);
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
    public async Task Resume_or_acquire_resumes_an_exact_adopted_owner()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset);

        gate.AdoptDurableRecoveryOwner(owner, scope: null, cleanupOnlyHistoricalCampaign: false);

        await using CovenantExclusiveLease resumed =
            (await gate.ResumeOrAcquireExclusiveAsync(owner, Token)).Value;

        Assert.Equal(owner, resumed.Snapshot.RecoveryOwner);
    }

    [Fact]
    public async Task Resume_or_acquire_acquires_only_when_no_closure_exists()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset);

        await using CovenantExclusiveLease acquired =
            (await gate.ResumeOrAcquireExclusiveAsync(owner, Token)).Value;

        Assert.Equal(owner, acquired.Snapshot.RecoveryOwner);
    }

    [Fact]
    public async Task Resume_or_acquire_refuses_a_conflicting_owner_without_replacing_it()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner winner =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset);

        CovenantExclusiveRecoveryOwner conflicting =
            CovenantOperationGateFixture.Owner(
                CovenantExclusiveOperation.CovenantReset,
                operationId: Guid.Parse("abababab-abab-4bab-8bab-abababababab"));

        gate.AdoptDurableRecoveryOwner(winner, scope: null, cleanupOnlyHistoricalCampaign: false);

        Result<CovenantExclusiveLease> refused = await gate.ResumeOrAcquireExclusiveAsync(
            conflicting,
            Token);

        Assert.True(refused.IsFailure);

        await using CovenantExclusiveLease resumed =
            (await gate.ResumeExclusiveAsync(winner, Token)).Value;

        Assert.Equal(winner, resumed.Snapshot.RecoveryOwner);
    }

    [Fact]
    public async Task Resume_or_acquire_cannot_bypass_a_closure_that_wins_at_the_decision_boundary()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantInstallationReadLease reader =
            (await gate.AcquireInstallationReadAsync(Token)).Value;

        CovenantExclusiveRecoveryOwner winner =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset);

        CovenantExclusiveRecoveryOwner loser =
            CovenantOperationGateFixture.Owner(
                CovenantExclusiveOperation.CovenantReset,
                operationId: Guid.Parse("cdcdcdcd-cdcd-4dcd-8dcd-cdcdcdcdcdcd"));

        Task<Result<CovenantExclusiveLease>> winning = gate
            .ResumeOrAcquireExclusiveAsync(winner, Token)
            .AsTask();

        Assert.True(reader.Revocation.IsCancellationRequested);

        Result<CovenantExclusiveLease> refused = await gate.ResumeOrAcquireExclusiveAsync(loser, Token);

        Assert.True(refused.IsFailure);

        await reader.DisposeAsync();

        await using CovenantExclusiveLease lease = (await winning).Value;

        Assert.Equal(winner, lease.Snapshot.RecoveryOwner);
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

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, resumed.Error.Code);
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

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, unjournaled.Error.Code);

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

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, outcome.Error.Code);

        Assert.Equal(1, finalizer.Invocations);

        Result retry = await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, Token);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, retry.Error.Code);
    }

    /// <summary>
    /// Once the gate has applied the disposition the operation is decided, whatever the finalizer then does.
    /// A finalizer that throws (a full disk failing the journal's commit, say) used to escape the lease after
    /// admission had already reopened, so a caller that read "the completion threw" as "the disposition did
    /// not happen" could undo work the rest of the system had begun to use. The lease now answers a failure
    /// the caller handles like any other finalizer failure: the journal stays nonterminal and the
    /// disposition stays spent. A cancellation is one of the faults, because the cancellation handler of a
    /// caller is the same wrong reader of this window.
    /// </summary>
    [Theory]
    [InlineData("sqlite")]
    [InlineData("io")]
    [InlineData("invalid-operation")]
    [InlineData("canceled")]
    public async Task A_finalizer_that_throws_is_a_failed_finalizer_after_a_spent_disposition(
        string fault)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveLease exclusive = (await gate.AcquireExclusiveAsync(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token)).Value;

        const string driverText = "driver text that must not reach the caller";

        Exception thrown = fault switch
        {
            "sqlite" => new Microsoft.Data.Sqlite.SqliteException(driverText, 13),
            "io" => new IOException(driverText),
            "canceled" => new OperationCanceledException(driverText),
            _ => new InvalidOperationException(driverText),
        };

        FaultingPostDispositionFinalizer finalizer = new(thrown);

        Result outcome = await exclusive.CompleteAsync(
            CovenantExclusiveLeaseDisposition.CommitAndReopen,
            finalizer,
            Token);

        Assert.True(outcome.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, outcome.Error.Code);

        Assert.Contains(thrown.GetType().Name, outcome.Error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(driverText, outcome.Error.Message, StringComparison.Ordinal);

        Assert.Equal(1, finalizer.Invocations);

        // The disposition was applied, so no other one can follow it, and admission is open.
        Result retry = await exclusive.CompleteAsync(CovenantExclusiveLeaseDisposition.RollbackAndReopen, Token);

        Assert.Equal(ErrorCodes.Covenant.LifecycleConflict, retry.Error.Code);

        await exclusive.DisposeAsync();

        await using CovenantReadLease reopened =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.Equal(CovenantScope.Global, reopened.Snapshot.Scope!.Value.Kind);
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
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                Token)).Error.Code);

        Assert.True((await transfer.RevalidateAsync(Token)).IsSuccess);
    }

    [Fact]
    public async Task Campaign_entry_erasure_closes_its_Campaign_and_installation_coverage_only()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantTurnLease campaignOneTurn = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        CovenantInstallationReadLease installationRead = (await gate.AcquireInstallationReadAsync(Token)).Value;

        await using CovenantTurnLease campaignTwoTurn = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        await using CovenantTurnLease globalTurn =
            (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, Token)).Value;

        await using CovenantCleanupLease globalCleanup =
            (await gate.AcquireCleanupAsync(CovenantOperationScope.Global, Token)).Value;

        Task<Result<CovenantEntryErasureLease>> close = gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token).AsTask();

        await WaitForAsync(() => campaignOneTurn.Revocation.IsCancellationRequested, Token);

        await WaitForAsync(() => installationRead.Revocation.IsCancellationRequested, Token);

        Assert.False(campaignTwoTurn.Revocation.IsCancellationRequested);

        Assert.False(globalTurn.Revocation.IsCancellationRequested);

        Assert.False(globalCleanup.Revocation.IsCancellationRequested);

        Assert.False(close.IsCompleted);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireTurnAsync(
                CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
                Token)).Error.Code);

        Assert.Equal(ErrorCodes.Covenant.Unavailable, (await gate.AcquireAcceleratorAsync(Token)).Error.Code);

        CovenantReadLease campaignTwoRead = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        Assert.Equal(CovenantOperationGateFixture.CampaignTwo, campaignTwoRead.Snapshot.Scope!.Value.CampaignId);

        await campaignTwoRead.DisposeAsync();

        await campaignOneTurn.DisposeAsync();

        await installationRead.DisposeAsync();

        Result<CovenantEntryErasureLease> acquired = await close;

        Assert.True(acquired.IsSuccess);

        await using CovenantEntryErasureLease lease = acquired.Value;

        Assert.False(lease.CoversInstallation);

        Assert.Equal(CovenantLeaseKind.EntryErasure, lease.Snapshot.Kind);

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, lease.Snapshot.Scope!.Value.CampaignId);

        Assert.Equal(
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            lease.Snapshot.RecoveryOwner);

        Assert.IsAssignableFrom<ICovenantSnapshotReadLease>(lease);

        Assert.IsAssignableFrom<ICovenantExclusiveOperationLease>(lease);
    }

    [Fact]
    public async Task Global_entry_erasure_takes_the_installation_slot_and_drains_every_turn()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantTurnLease campaignOneTurn = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        CovenantTurnLease campaignTwoTurn = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        CovenantTurnLease globalTurn =
            (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, Token)).Value;

        Task<Result<CovenantEntryErasureLease>> close = gate.AcquireEntryErasureAsync(
            CovenantOperationScope.Global,
            reclaimsKey: false,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token).AsTask();

        // A Global entry is read by every Campaign's turns, so every one of them is told to stop.
        await WaitForAsync(() => campaignOneTurn.Revocation.IsCancellationRequested, Token);

        await WaitForAsync(() => campaignTwoTurn.Revocation.IsCancellationRequested, Token);

        await WaitForAsync(() => globalTurn.Revocation.IsCancellationRequested, Token);

        Assert.False(close.IsCompleted);

        await campaignOneTurn.DisposeAsync();

        await campaignTwoTurn.DisposeAsync();

        await globalTurn.DisposeAsync();

        Result<CovenantEntryErasureLease> acquired = await close;

        Assert.True(acquired.IsSuccess);

        await using CovenantEntryErasureLease lease = acquired.Value;

        Assert.True(lease.CoversInstallation);

        Assert.Null(lease.Snapshot.Scope);

        Assert.Equal(CovenantLeaseKind.EntryErasure, lease.Snapshot.Kind);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
                Token)).Error.Code);
    }

    [Fact]
    public async Task A_reclaiming_Campaign_entry_erasure_takes_the_installation_slot()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantTurnLease campaignTwoTurn = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignTwo),
            Token)).Value;

        Task<Result<CovenantEntryErasureLease>> close = gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: true,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token).AsTask();

        // Reclamation removes the key's curation in every scope, so another Campaign's turn drains.
        await WaitForAsync(() => campaignTwoTurn.Revocation.IsCancellationRequested, Token);

        Assert.False(close.IsCompleted);

        await campaignTwoTurn.DisposeAsync();

        Result<CovenantEntryErasureLease> acquired = await close;

        Assert.True(acquired.IsSuccess);

        await using CovenantEntryErasureLease lease = acquired.Value;

        Assert.True(lease.CoversInstallation);

        Assert.Null(lease.Snapshot.Scope);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);
    }

    [Theory]
    [InlineData(CovenantCampaignScopeState.Deleted, false, ErrorCodes.Covenant.LifecycleConflict)]
    [InlineData(CovenantCampaignScopeState.Deleted, true, ErrorCodes.Covenant.LifecycleConflict)]
    [InlineData(CovenantCampaignScopeState.Unknown, false, ErrorCodes.Covenant.NotFound)]
    [InlineData(CovenantCampaignScopeState.Unknown, true, ErrorCodes.Covenant.NotFound)]
    public async Task Campaign_entry_erasure_refuses_a_deleted_or_unknown_Campaign(
        CovenantCampaignScopeState state,
        bool reclaimsKey,
        string expectedCode)
    {
        FakeCovenantCampaignScopeProbe campaigns = new();

        campaigns.Set(CovenantOperationGateFixture.CampaignOne, state);

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(campaigns: campaigns);

        Result<CovenantEntryErasureLease> refused = await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token);

        Assert.Equal(expectedCode, refused.Error.Code);

        // The refusal came before any scope closed.
        await using CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_erasure_requires_an_initialized_entry_scope(bool reclaimsKey)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantEntryErasureLease> refused = await gate.AcquireEntryErasureAsync(
            default,
            reclaimsKey,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token);

        Assert.Equal(ErrorCodes.Covenant.InvalidScope, refused.Error.Code);

        await using CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
    }

    /// <summary>
    /// The owner's operation code is judged before the scope is looked at, so an owner this shape cannot
    /// serve is refused as a wrong shape whatever it is paired with.
    /// </summary>
    /// <remarks>
    /// Paired with a scope that would answer <c>InvalidScope</c> on its own, so an acquisition that looked
    /// at the scope first would give a different, misleading diagnosis for the same mistake.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entry_erasure_refuses_a_wrong_owner_before_it_looks_at_the_scope(bool reclaimsKey)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        Result<CovenantEntryErasureLease> refused = await gate.AcquireEntryErasureAsync(
            default,
            reclaimsKey,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantReset),
            Token);

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, refused.Error.Code);

        await using CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value;

        Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
    }

    [Fact]
    public async Task Entry_erasure_owner_is_never_adopted_durably()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure);

        // The exact type and the diagnosis, not any ArgumentException: an entry erasure is refused for
        // having no durable owner by design, which is a different diagnosis from an unclassified
        // operation code, and the two differ in type and in what they say.
        ArgumentException global = Assert.Throws<ArgumentException>(
            () => gate.AdoptDurableRecoveryOwner(owner, scope: null, cleanupOnlyHistoricalCampaign: false));

        ArgumentException campaign = Assert.Throws<ArgumentException>(
            () => gate.AdoptDurableRecoveryOwner(
                owner,
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                cleanupOnlyHistoricalCampaign: false));

        foreach (ArgumentException refusal in new[] { global, campaign })
        {
            Assert.Equal("owner", refusal.ParamName);

            Assert.Contains("never has a durable recovery owner", refusal.Message, StringComparison.Ordinal);
        }

        // Neither refusal installed a closure that no later process could ever resume: the installation
        // reads, and the same entry erasure the refused adoption named is still free to be taken.
        await using (CovenantInstallationReadLease open = (await gate.AcquireInstallationReadAsync(Token)).Value)
        {
            Assert.Equal(CovenantLeaseKind.InstallationRead, open.Snapshot.Kind);
        }

        await using CovenantEntryErasureLease erasure = (await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            owner,
            Token)).Value;

        Assert.Equal(CovenantLeaseKind.EntryErasure, erasure.Snapshot.Kind);
    }

    [Fact]
    public async Task An_undrained_turn_refuses_the_entry_erasure_and_reopens()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(
            drainTimeout: TimeSpan.FromMilliseconds(150));

        await using CovenantTurnLease held = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        Result<CovenantEntryErasureLease> refused = await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token);

        Assert.Equal(ErrorCodes.Covenant.MaintenanceFailed, refused.Error.Code);

        Assert.True(held.Revocation.IsCancellationRequested);

        await using CovenantTurnLease admitted = (await gate.AcquireTurnAsync(
            CovenantOperationGateFixture.CampaignContext(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, admitted.Snapshot.Scope!.Value.CampaignId);
    }

    [Fact]
    public async Task A_closed_entry_erasure_scope_refuses_a_second_closure_without_waiting()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        await using CovenantEntryErasureLease first = (await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            Token)).Value;

        CovenantExclusiveRecoveryOwner second = CovenantOperationGateFixture.Owner(
            CovenantExclusiveOperation.CovenantEntryErasure,
            operationId: new Guid("66666666-6666-4666-8666-666666666666"));

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireEntryErasureAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                reclaimsKey: false,
                second,
                Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireEntryErasureAsync(CovenantOperationScope.Global, reclaimsKey: false, second, Token))
                .Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireCampaignExclusiveAsync(
                CovenantOperationGateFixture.CampaignOne,
                CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CampaignDelete),
                Token)).Error.Code);

        await using CovenantEntryErasureLease otherCampaign = (await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignTwo),
            reclaimsKey: false,
            second,
            Token)).Value;

        Assert.Equal(CovenantOperationGateFixture.CampaignTwo, otherCampaign.Snapshot.Scope!.Value.CampaignId);
    }

    [Fact]
    public async Task A_kept_closed_entry_erasure_has_no_resume_shape()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantExclusiveRecoveryOwner owner =
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure);

        CovenantEntryErasureLease lease = (await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            owner,
            Token)).Value;

        Assert.True((await lease.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, Token)).IsSuccess);

        await lease.DisposeAsync();

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                Token)).Error.Code);

        // Nothing in this process can take the closure over; only a fresh process, which never adopts
        // an entry-erasure owner, starts without it.
        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.ResumeCampaignExclusiveAsync(CovenantOperationGateFixture.CampaignOne, owner, Token))
                .Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await gate.ResumeExclusiveAsync(owner, Token)).Error.Code);

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireEntryErasureAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                reclaimsKey: false,
                owner,
                Token)).Error.Code);
    }

    [Theory]
    [InlineData(CovenantExclusiveLeaseDisposition.RollbackAndReopen)]
    [InlineData(CovenantExclusiveLeaseDisposition.CommitAndReopen)]
    public async Task Completing_with_CancellationToken_None_after_the_request_was_cancelled_reopens_the_scope(
        CovenantExclusiveLeaseDisposition disposition)
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        using CancellationTokenSource request = new();

        await using CovenantEntryErasureLease lease = (await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            request.Token)).Value;

        await request.CancelAsync();

        Assert.True((await lease.CompleteAsync(disposition, CancellationToken.None)).IsSuccess);

        await using CovenantReadLease reopened = (await gate.AcquireReadAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            Token)).Value;

        Assert.Equal(CovenantOperationGateFixture.CampaignOne, reopened.Snapshot.Scope!.Value.CampaignId);
    }

    /// <summary>
    /// Why the entry erasure must complete every disposition with <see cref="CancellationToken.None"/>:
    /// the request's own token, once cancelled, consumes the one disposition without reopening.
    /// </summary>
    [Fact]
    public async Task Completing_with_the_cancelled_request_token_leaves_the_scope_closed()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        using CancellationTokenSource request = new();

        CovenantEntryErasureLease lease = (await gate.AcquireEntryErasureAsync(
            CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
            reclaimsKey: false,
            CovenantOperationGateFixture.Owner(CovenantExclusiveOperation.CovenantEntryErasure),
            request.Token)).Value;

        await request.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await lease.CompleteAsync(
                CovenantExclusiveLeaseDisposition.RollbackAndReopen,
                request.Token));

        await lease.DisposeAsync();

        Assert.Equal(
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                Token)).Error.Code);
    }

    [Fact]
    public async Task Revalidation_notices_a_committed_dataset_generation_change()
    {
        FakeCovenantAvailability availability = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantReadLease reader =
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        Assert.True((await reader.RevalidateAsync(Token)).IsSuccess);

        availability.PublishCommittedDataset(Guid.NewGuid());

        Assert.Equal(
            ErrorCodes.Covenant.ForbiddenAuthority,
            (await reader.RevalidateAsync(Token)).Error.Code);
    }

    [Fact]
    public async Task Revalidation_notices_an_authority_epoch_change()
    {
        FakeCovenantAuthorityProvider authority = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(authority: authority);

        await using CovenantTurnLease turn =
            (await gate.AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, Token)).Value;

        authority.Advance();

        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, (await turn.RevalidateAsync(Token)).Error.Code);
    }

    [Fact]
    public async Task Revalidation_notices_an_accelerator_epoch_change()
    {
        FakeCovenantAvailability availability = new();

        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate(availability);

        await using CovenantAcceleratorLease accelerator = (await gate.AcquireAcceleratorAsync(Token)).Value;

        availability.Mutate(current => current with { AcceleratorEpoch = current.AcceleratorEpoch + 1 });

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, (await accelerator.RevalidateAsync(Token)).Error.Code);
    }

    [Fact]
    public async Task A_disposed_lease_cannot_be_used_late()
    {
        CovenantOperationGate gate = CovenantOperationGateFixture.CreateGate();

        CovenantReadLease reader = (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Value;

        await reader.DisposeAsync();

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, (await reader.RevalidateAsync(Token)).Error.Code);

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
            ErrorCodes.Covenant.OperatorAuthorityUnavailable,
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
            ErrorCodes.Covenant.Unavailable,
            (await gate.AcquireReadAsync(CovenantOperationScope.Global, Token)).Error.Code);
    }

    /// <summary>
    /// Every gate entry point that takes a recovery owner, each judged on a fresh gate by whether it
    /// refused the owner's operation code as the wrong shape.
    /// </summary>
    private static readonly (string Shape, Func<CovenantOperationGate, CovenantExclusiveRecoveryOwner, Task<bool>> Admits)[] ExclusiveShapes =
    [
        ("exclusive", static (gate, owner) => AdmitsAsync(gate.AcquireExclusiveAsync(owner, Token))),
        ("resume-or-acquire-exclusive", static (gate, owner) => AdmitsAsync(gate.ResumeOrAcquireExclusiveAsync(owner, Token))),
        ("resume-exclusive", static (gate, owner) => AdmitsAsync(gate.ResumeExclusiveAsync(owner, Token))),
        ("campaign-exclusive", static (gate, owner) => AdmitsAsync(
            gate.AcquireCampaignExclusiveAsync(CovenantOperationGateFixture.CampaignOne, owner, Token))),
        ("resume-campaign-exclusive", static (gate, owner) => AdmitsAsync(
            gate.ResumeCampaignExclusiveAsync(CovenantOperationGateFixture.CampaignOne, owner, Token))),
        ("protected-transfer", static (gate, owner) => AdmitsAsync(
            gate.AcquireProtectedTransferAsync(
                ProtectedTransferScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                owner,
                Token))),
        ("resume-protected-transfer", static (gate, owner) => AdmitsAsync(
            gate.ResumeProtectedTransferAsync(
                ProtectedTransferScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                owner,
                Token))),
        ("entry-erasure", static (gate, owner) => AdmitsAsync(
            gate.AcquireEntryErasureAsync(
                CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne),
                reclaimsKey: false,
                owner,
                Token))),
        ("adopt-installation", static (gate, owner) => Task.FromResult(Adopts(gate, owner, scope: null))),
        ("adopt-scoped", static (gate, owner) => Task.FromResult(
            Adopts(gate, owner, CovenantOperationScope.ForCampaign(CovenantOperationGateFixture.CampaignOne)))),
    ];

    /// <summary>
    /// Whether an acquisition or resume got past its operation-code check. A resume on a fresh gate
    /// that passes the check still finds no closure, which is a different refusal than the shape one.
    /// </summary>
    private static async Task<bool> AdmitsAsync<TLease>(ValueTask<Result<TLease>> attempt)
        where TLease : CovenantOperationLease
    {
        Result<TLease> result = await attempt;

        if (result.IsSuccess)
        {
            await result.Value.DisposeAsync();

            return true;
        }

        return result.Error.Code != ErrorCodes.Covenant.ForbiddenAuthority;
    }

    private static CovenantExclusiveRecoveryOwner ForgeOperation(
        CovenantExclusiveRecoveryOwner owner,
        CovenantExclusiveOperation operation)
    {
        object boxed = owner;

        typeof(CovenantExclusiveRecoveryOwner)
            .GetField(
                $"<{nameof(CovenantExclusiveRecoveryOwner.Operation)}>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(boxed, operation);

        CovenantExclusiveRecoveryOwner forged = (CovenantExclusiveRecoveryOwner)boxed;

        Assert.Equal(operation, forged.Operation);

        return forged;
    }

    private static bool Adopts(
        CovenantOperationGate gate,
        CovenantExclusiveRecoveryOwner owner,
        CovenantOperationScope? scope)
    {
        try
        {
            gate.AdoptDurableRecoveryOwner(owner, scope, cleanupOnlyHistoricalCampaign: false);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
