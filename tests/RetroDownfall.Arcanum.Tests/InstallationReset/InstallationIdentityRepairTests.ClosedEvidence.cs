using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// What the repair does with the restore and offline-transition evidence a profile keeps: unfinished
/// work blocks it, the tombstone of finished work names the identity to put back, and a key with no
/// anchor is no evidence at all.
/// </summary>
/// <remarks>
/// The anchors here are written in the exact form the stores write them, so the repair reads the same
/// bytes a real history leaves. The end-to-end histories (a real global reset, a real replacement
/// restore) are pinned in <c>InstallationResetIdentityContinuityTests</c>.
/// </remarks>
public sealed partial class InstallationIdentityRepairTests
{
    private static readonly Guid Named = Guid.Parse("0a0b0c0d-1111-4222-8333-444455556666");

    private static readonly Guid OtherNamed = Guid.Parse("f0f1f2f3-aaaa-4bbb-8ccc-ddddeeeeffff");

    [Theory]
    [InlineData("restore")]
    [InlineData("offline transition")]
    public async Task Unfinished_work_bound_to_the_identity_blocks_the_repair(string slot)
    {
        SetIdentity(DamagedValue);

        SetAnchor(slot, Named, closed: false);

        InstallationIdentityRepair repair = Create();

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Data.Blocked, "is unfinished");

            Assert.DoesNotContain("Finish or close", result.Error.Message, StringComparison.Ordinal);
        }

        AssertIdentityIs(DamagedValue);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("offline transition")]
    public async Task A_closed_anchor_names_the_identity_the_repair_writes_back_and_never_removes(string slot)
    {
        SetIdentity(DamagedValue);

        SetAnchor(slot, Named, closed: true);

        InstallationIdentityRepair repair = Create();

        Result<InstallationIdentityRepairReport> planned = await repair.PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Damaged, planned.Value.State);

        Assert.Equal(InstallationIdentityRepairAction.Restore, planned.Value.Action);

        // The plan changes nothing.
        AssertIdentityIs(DamagedValue);

        int setsBefore = _credentials.SetCount;

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Restored, applied.Value.State);

        Assert.Equal(InstallationIdentityRepairAction.Restore, applied.Value.Action);

        Assert.Equal(IdentityAccount(), applied.Value.CredentialAccount);

        // One write of the item, no delete, and the item now holds the identity the anchor names, in
        // its one canonical spelling.
        Assert.Equal(setsBefore + 1, _credentials.SetCount);

        Assert.Equal(0, _credentials.DeleteCount);

        AssertIdentityIs(Named.ToString("D").ToUpperInvariant());

        // The anchor is the tombstone it was, and is repeatable: the second run finds a valid identity.
        Result<InstallationIdentityRepairReport> again = await repair.ApplyAsync();

        Assert.Equal(ErrorCodes.Data.Conflict, again.Error.Code);
    }

    [Fact]
    public async Task Both_anchors_closed_over_one_installation_write_it_back_once()
    {
        SetIdentity("not an identity");

        SetAnchor("restore", Named, closed: true);

        SetAnchor("offline transition", Named, closed: true);

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Restored, applied.Value.State);

        Assert.Equal(0, _credentials.DeleteCount);

        AssertIdentityIs(Named.ToString("D").ToUpperInvariant());
    }

    [Fact]
    public async Task Closed_anchors_that_name_different_installations_leave_no_one_identity_to_put_back()
    {
        SetIdentity(DamagedValue);

        SetAnchor("restore", Named, closed: true);

        SetAnchor("offline transition", OtherNamed, closed: true);

        InstallationIdentityRepair repair = Create();

        foreach (Result<InstallationIdentityRepairReport> result in new[]
                 {
                     await repair.PlanAsync(),
                     await repair.ApplyAsync(),
                 })
        {
            AssertRefused(result, ErrorCodes.Covenant.ManualRecoveryRequired, "different installations");
        }

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task One_active_anchor_blocks_the_repair_whatever_the_closed_one_names()
    {
        SetIdentity(DamagedValue);

        SetAnchor("restore", Named, closed: true);

        SetAnchor("offline transition", Named, closed: false);

        AssertRefused(await Create().ApplyAsync(), ErrorCodes.Data.Blocked, "offline Grimoire transition is unfinished");

        AssertIdentityIs(DamagedValue);
    }

    [Theory]
    [InlineData("restore journal key")]
    [InlineData("offline transition journal key")]
    public async Task A_key_with_no_anchor_names_nothing_and_the_damaged_identity_is_removed(string key)
    {
        SetIdentity(DamagedValue);

        SetOther(EvidenceAccount(key));

        Result<InstallationIdentityRepairReport> planned = await Create().PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairAction.Remove, planned.Value.Action);

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Removed, applied.Value.State);

        Assert.Equal([IdentityAccount()], _credentials.Deleted);

        // The key was not touched.
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.TryGet(ArcanumCredentialIdentity.Service, EvidenceAccount(key)).Status);
    }

    [Theory]
    [InlineData("restore journal anchor")]
    [InlineData("offline transition journal anchor")]
    public async Task An_anchor_that_cannot_be_read_as_an_anchor_is_refused_and_not_ignored(string anchor)
    {
        SetIdentity(DamagedValue);

        SetOther(EvidenceAccount(anchor));

        AssertRefused(
            await Create().ApplyAsync(),
            ErrorCodes.Covenant.ManualRecoveryRequired,
            "could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Theory]
    [InlineData("restore journal anchor", false)]
    [InlineData("restore journal anchor", true)]
    [InlineData("offline transition journal anchor", false)]
    [InlineData("offline transition journal anchor", true)]
    public async Task An_anchor_the_store_will_not_answer_for_is_an_outage_and_nothing_changes(
        string anchor,
        bool throws)
    {
        SetIdentity(DamagedValue);

        _credentials.UnreadableAccounts.Add(EvidenceAccount(anchor));

        _credentials.ThrowOnUnreadable = throws;

        AssertRefused(await Create().ApplyAsync(), ErrorCodes.Covenant.Unavailable, "could not be proved");

        AssertIdentityIs(DamagedValue);
    }

    [Fact]
    public async Task A_valid_identity_is_never_replaced_even_when_an_anchor_names_another_installation()
    {
        SetIdentity(CanonicalValue);

        SetAnchor("restore", Named, closed: true);

        Result<InstallationIdentityRepairReport> applied = await Create().ApplyAsync();

        AssertRefused(applied, ErrorCodes.Data.Conflict, "valid canonical identity");

        AssertIdentityIs(CanonicalValue);
    }

    [Fact]
    public async Task An_identity_that_vanishes_between_the_plan_and_the_apply_is_not_written_back()
    {
        SetIdentity(DamagedValue);

        SetAnchor("restore", Named, closed: true);

        InstallationIdentityRepair repair = Create();

        Assert.True((await repair.PlanAsync()).IsSuccess);

        _ = _inner.Delete(ArcanumCredentialIdentity.Service, IdentityAccount());

        int setsBefore = _credentials.SetCount;

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess);

        Assert.Equal(InstallationIdentityRepairState.Absent, applied.Value.State);

        Assert.Equal(setsBefore, _credentials.SetCount);

        Assert.Equal(
            OsCredentialStoreStatus.NotFound,
            _inner.TryGet(ArcanumCredentialIdentity.Service, IdentityAccount()).Status);
    }

    private void SetAnchor(string slot, Guid installation, bool closed)
    {
        if (slot is "restore")
        {
            SetRestoreAnchor(
                installation,
                closed ? BackupRestoreJournalAnchorState.Closed : BackupRestoreJournalAnchorState.Active);

            return;
        }

        SetTransitionAnchor(
            installation,
            closed ? GrimoireOfflineTransitionAnchorState.Closed : GrimoireOfflineTransitionAnchorState.Active);
    }

    private void SetRestoreAnchor(Guid installation, BackupRestoreJournalAnchorState state)
    {
        BackupRestoreJournalAnchorV1 anchor = new(
            Version: 1,
            _profile.Digest,
            installation,
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Revision: 3,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x11, 32)]),
            new CovenantDigest([.. Enumerable.Repeat((byte)0x22, 32)]),
            state);

        Result<string> encoded = BackupRestoreJournalAuthenticator.EncodeAnchor(anchor);

        Assert.True(encoded.IsSuccess, encoded.IsFailure ? encoded.Error.Message : string.Empty);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.Set(
                ArcanumCredentialIdentity.Service,
                EvidenceAccount("restore journal anchor"),
                encoded.Value).Status);
    }

    private void SetTransitionAnchor(Guid installation, GrimoireOfflineTransitionAnchorState state)
    {
        // The location is committed to the physical identity of the parent, which is only safe to resolve
        // once the lock has made the root an owner-only directory, as the repair's own first step does.
        ArcanumMaintenanceLockAcquisitionResult acquired = ArcanumMaintenanceLock.AcquireDetailed(_root);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        GrimoireGuardedRootTopology.EnsureOwnedRootIsSafe(held, _root);

        GrimoireOfflineTransitionJournalLocation location = new GrimoireOfflineTransitionJournalFileStore()
            .ResolveLocation(_root)
            .Value;

        GrimoireOfflineTransitionAnchorV1 anchor = new(
            Version: 1,
            _profile.Digest,
            installation,
            SlotEpoch: 1,
            state,
            Guid.Parse("33333333-3333-4333-8333-333333333333"),
            GrimoireOfflineTransitionKind.CovenantReset,
            PayloadVersion: 1,
            Revision: 2,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x44, 32)]),
            location.JournalLocationDigest);

        Result<string> encoded = GrimoireOfflineTransitionJournalAuthenticator.EncodeAnchor(anchor);

        Assert.True(encoded.IsSuccess, encoded.IsFailure ? encoded.Error.Message : string.Empty);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _inner.Set(
                ArcanumCredentialIdentity.Service,
                EvidenceAccount("offline transition journal anchor"),
                encoded.Value).Status);
    }
}
