using System.Text;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The repair of a damaged installation identity over the histories that really leave a profile with
/// one: the evidence each finished piece of work keeps is closed, not absent, and the repair has to
/// put the identity back rather than strand every later reset.
/// </summary>
/// <remarks>
/// The unit cases write anchors in the form the stores write them. These run the real work: a global
/// reset through the stopped-host composition and the protected erasure, and a replacement restore
/// through the restore service, and then damage the one credential item the next start reads.
/// </remarks>
public sealed partial class InstallationResetIdentityContinuityTests
{
    private const string DamagedIdentity = "c0ffee00-1234-4abc-8def-0123456789ab";

    /// <summary>
    /// A global reset opens a transition journal for its nested erasure and keeps the closed anchor of
    /// it, with the identity it names. A damaged identity after it is written back from that anchor, so
    /// the Grimoire born next takes the same installation and a later transition can open beside it.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_after_a_global_reset_is_written_back_from_the_kept_anchor_and_a_later_transition_opens()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        await InstallPriorGrimoireAsync(provider);

        Guid prior = await ReadDatabaseIdentityAsync(provider);

        Result<InstallationResetResult> reset = await FactoryResetAsync(provider);

        Assert.True(reset.IsSuccess, reset.IsFailure ? reset.Error.Message : string.Empty);

        Assert.Equal(InstallationResetPhase.Completed, reset.Value.Phase);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        // The premise: the work that finished kept its tombstone, closed, naming the installation.
        GrimoireOfflineTransitionAnchorV1 tombstone = TransitionAnchor();

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, tombstone.State);

        Assert.Equal(prior, tombstone.InstallationId);

        DamageTheIdentity();

        InstallationIdentityRepairReport repaired = await RepairAsync(provider);

        Assert.Equal(InstallationIdentityRepairState.Restored, repaired.State);

        Assert.Equal(prior, Guid.Parse(RetainedIdentity()!));

        // The next start adopts it, and a Covenant reset's transition opens beside the kept anchor.
        await BootstrapGrimoireAsync(provider);

        Guid fresh = await ReadDatabaseIdentityAsync(provider);

        Assert.Equal(prior, fresh);

        await AssertATransitionOpensAsync(fresh);
    }

    /// <summary>
    /// The same, after a replacement restore, whose own anchor is closed when it finishes, with the
    /// database later gone from the profile.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_after_a_replacement_restore_is_written_back_from_the_closed_restore_anchor()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        Rebirth rebirth = await ResetAndRebirthAsync();

        await ReplacementRestoreAsync(rebirth);

        BackupRestoreProfileNamespace profile = BackupRestoreJournalAuthenticator
            .ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory)
            .Value;

        BackupRestoreJournalAnchorV1 tombstone = Assert.IsType<BackupRestoreJournalAnchorV1>(
            new BackupRestoreJournalAnchorStore(
                    _credentials,
                    new BackupRestoreJournalKeyProvider(_credentials),
                    new BackupRestoreJournalInstallationIdentityProvider(_credentials))
                .TryReadAnchor(profile)
                .Value);

        Assert.Equal(BackupRestoreJournalAnchorState.Closed, tombstone.State);

        // The operator moves the database aside: no Grimoire exists for the identity to be the binding of.
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm", ".kdf" })
        {
            File.Delete(ArcanumPaths.GrimoireDatabaseFile + suffix);
        }

        // Both finished works name one installation, so the repair has one identity to put back.
        Assert.Equal(tombstone.InstallationId, TransitionAnchor().InstallationId);

        DamageTheIdentity();

        InstallationIdentityRepairReport repaired = await RepairAsync(rebirth.Provider);

        Assert.Equal(InstallationIdentityRepairState.Restored, repaired.State);

        Assert.Equal(tombstone.InstallationId, Guid.Parse(RetainedIdentity()!));
    }

    private async Task<InstallationIdentityRepairReport> RepairAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();

        InstallationIdentityRepair repair = scope.ServiceProvider.GetRequiredService<InstallationIdentityRepair>();

        Result<InstallationIdentityRepairReport> planned = await repair.PlanAsync();

        Assert.True(planned.IsSuccess, planned.IsFailure ? planned.Error.Message : string.Empty);

        Assert.Equal(InstallationIdentityRepairState.Damaged, planned.Value.State);

        Assert.Equal(InstallationIdentityRepairAction.Restore, planned.Value.Action);

        Result<InstallationIdentityRepairReport> applied = await repair.ApplyAsync();

        Assert.True(applied.IsSuccess, applied.IsFailure ? applied.Error.Message : string.Empty);

        return applied.Value;
    }

    private void DamageTheIdentity()
    {
        BackupRestoreProfileNamespace profile = BackupRestoreJournalAuthenticator
            .ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory)
            .Value;

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profile.AccountSuffix),
                DamagedIdentity).Status);
    }

    private GrimoireOfflineTransitionAnchorV1 TransitionAnchor()
    {
        GrimoireOfflineTransitionJournalLocation location = new GrimoireOfflineTransitionJournalFileStore()
            .ResolveLocation(ArcanumPaths.GrimoireDirectory)
            .Value;

        return Assert.IsType<GrimoireOfflineTransitionAnchorV1>(
            new GrimoireOfflineTransitionJournalAnchorStore(_credentials).Read(location).Value);
    }

    private async Task AssertATransitionOpensAsync(Guid installation)
    {
        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        GrimoireOfflineTransitionJournalLocation location = new GrimoireOfflineTransitionJournalFileStore()
            .ResolveLocation(ArcanumPaths.GrimoireDirectory)
            .Value;

        // The two calls GrimoireOfflineTransitionPhaseAuthority makes, in its order, before a Covenant
        // reset's journal can open: seed-or-compare the profile identity with the database row, then
        // begin the journal bound to that same row.
        Result<Guid> seeded = new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(held, ArcanumPaths.GrimoireDirectory, location.ProfileNamespace, installation);

        Assert.True(seeded.IsSuccess, seeded.IsFailure ? seeded.Error.Message : string.Empty);

        Result<GrimoireOfflineTransitionJournalPublication> begun = await new GrimoireOfflineTransitionJournalStore(
                _credentials)
            .BeginAsync(
                held,
                ArcanumPaths.GrimoireDirectory,
                installation,
                Guid.NewGuid(),
                GrimoireOfflineTransitionKind.CovenantReset,
                payloadVersion: 1,
                Encoding.UTF8.GetBytes("covenant-reset"),
                CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : string.Empty);
    }
}
