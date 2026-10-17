using System.Text;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.DataLifecycle;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Backup;

using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;

using RetroDownfall.Arcanum.Infrastructure.GrimoireTransitions;

using RetroDownfall.Arcanum.Infrastructure.Hosting;

using RetroDownfall.Arcanum.Infrastructure.InstallationReset;

using RetroDownfall.Arcanum.Infrastructure.Security;

using RetroDownfall.Arcanum.Secrets.Security;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.InstallationReset;

/// <summary>
/// The host's own start over a profile whose installation identity is damaged: the refusal that names
/// the repair is reached from every history that leaves one, the repair puts the profile right, and the
/// next start creates the Grimoire under the identity the finished work names.
/// </summary>
/// <remarks>
/// These start the real <see cref="GrimoireDatabaseHostedService"/> over the real recovery chain (the
/// reset record, the offline-transition journal and the restore journal), because the order of those
/// reads is the property: a finished reset or transition keeps a closed anchor, the pair recovery that
/// reads it runs before the bootstrap that would otherwise refuse, and a damaged identity stops the
/// pair first. A refusal worded for the pair told the operator nothing about the command that clears
/// it, and the console shows only a Grimoire-unavailable refusal's message.
/// </remarks>
public sealed partial class InstallationResetIdentityContinuityTests
{
    private const string HostStartApiKey = "host-start-master-api-key";

    /// <summary>
    /// The reset leaves a closed transition anchor naming the installation of the database it swept
    /// away. The start with a damaged identity beside it is refused with the command that clears it,
    /// every time and without changing the item, the repair writes the identity back from the anchor,
    /// and the Grimoire the next start creates takes it.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_after_a_global_reset_refuses_every_host_start_naming_the_repair_and_the_repaired_profile_starts()
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

        // The premise: the finished reset kept the tombstone of its nested erasure, closed, naming the
        // installation the database carried, which is the anchor the pair recovery reads on every start.
        GrimoireOfflineTransitionAnchorV1 tombstone = TransitionAnchor();

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, tombstone.State);

        Assert.Equal(prior, tombstone.InstallationId);

        DamageTheIdentity();

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider);

        InstallationIdentityRepairReport repaired = await RepairAsync(provider);

        Assert.Equal(InstallationIdentityRepairState.Restored, repaired.State);

        await StartAndStopTheHostAsync(provider);

        // The new Grimoire adopted the identity the profile carries, which is the one the finished
        // reset's anchor names, so a later transition and a later reset open beside it.
        Assert.Equal(prior, await ReadDatabaseIdentityAsync(provider));

        Assert.Equal(prior, Guid.Parse(RetainedIdentity()!));

        await AssertATransitionOpensAsync(prior);
    }

    /// <summary>
    /// The same for a profile whose only kept evidence is the closed anchor of a Covenant reset: no
    /// reset record and no database, and the pair recovery still reads the anchor before the bootstrap.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_beside_a_closed_Covenant_reset_anchor_refuses_every_host_start_naming_the_repair_and_the_repaired_profile_starts()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        Guid installation = Guid.Parse("5b1f0c1e-7d52-4f0a-9a43-1c2d3e4f5a6b");

        await OpenAndRetireACovenantResetAsync(installation);

        GrimoireOfflineTransitionAnchorV1 tombstone = TransitionAnchor();

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, tombstone.State);

        Assert.Equal(installation, tombstone.InstallationId);

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        DamageTheIdentity();

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider);

        InstallationIdentityRepairReport repaired = await RepairAsync(provider);

        Assert.Equal(InstallationIdentityRepairState.Restored, repaired.State);

        await StartAndStopTheHostAsync(provider);

        Assert.Equal(installation, await ReadDatabaseIdentityAsync(provider));

        Assert.Equal(installation, Guid.Parse(RetainedIdentity()!));
    }

    /// <summary>
    /// The restore-journal sweep that runs inside the bootstrap does not read the identity beside a
    /// closed anchor, so a profile that keeps only a closed restore anchor reaches the bootstrap's own
    /// refusal, which names the repair: that sweep has no ordering to correct.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_beside_a_closed_restore_anchor_reaches_the_bootstraps_refusal_naming_the_repair()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        WriteAClosedRestoreAnchor(Guid.Parse("7c2e1d3f-8e63-4a1b-8b54-2d3e4f5a6b7c"));

        DamageTheIdentity();

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider);

        InstallationIdentityRepairReport repaired = await RepairAsync(provider);

        Assert.Equal(InstallationIdentityRepairState.Restored, repaired.State);

        await StartAndStopTheHostAsync(provider);

        Assert.Equal(
            Guid.Parse("7c2e1d3f-8e63-4a1b-8b54-2d3e4f5a6b7c"),
            await ReadDatabaseIdentityAsync(provider));
    }

    /// <summary>
    /// An unfinished offline transition keeps its identity: the start is refused, the refusal names
    /// the command and the condition under which it will not act, and the item is not touched.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_beside_an_unfinished_transition_refuses_the_start_and_leaves_the_item_alone()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        Guid installation = Guid.Parse("6a0e2f4d-9f74-4b2c-9c65-3e4f5a6b7c8d");

        await OpenACovenantResetAsync(installation, retire: false);

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Active, TransitionAnchor().State);

        DamageTheIdentity();

        GrimoireDatabaseUnavailableException refused = await AssertHostRefusesAsync(provider);

        AssertNamesTheRepair(refused);

        Assert.Contains("offline transition or installation reset is unfinished", refused.Message, StringComparison.Ordinal);

        Assert.Equal(DamagedIdentity, RetainedIdentity());

        Assert.False(File.Exists(ArcanumPaths.GrimoireDatabaseFile));
    }

    /// <summary>
    /// A refusal that is not about the identity keeps the pair's own words. A pair that cannot read its
    /// evidence for another reason is not told to run the repair.
    /// </summary>
    [SkippableFact]
    public async Task A_pair_failure_that_is_not_the_damaged_identity_is_not_told_to_run_the_repair()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        Guid installation = Guid.Parse("4f9d1e3c-8e63-4a1b-8b54-1c2d3e4f5a6b");

        await OpenAndRetireACovenantResetAsync(installation);

        // A canonical identity that names a different installation than the anchor: the store answered
        // and the value is well formed, so there is nothing for the repair to put right.
        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(
                ArcanumCredentialIdentity.Service,
                IdentityAccount(),
                Guid.Parse("0a0b0c0d-1111-4222-8333-444455556666").ToString("D").ToUpperInvariant()).Status);

        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => CreateHost(provider).StartAsync(CancellationToken.None));

        Assert.Contains(
            "Installation reset recovery state could not be read safely",
            refused.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "repair-installation-identity",
            refused.Message,
            StringComparison.Ordinal);

        Assert.IsNotType<GrimoireDatabaseUnavailableException>(refused);
    }

    /// <summary>
    /// The profile keeps its Grimoire, with its authority row, and the closed anchor of a standalone
    /// Covenant reset over it. A damaged identity stops every start at the pair recovery with the refusal
    /// that names the repair, in words that are true of a Grimoire that exists; the repair seeds the
    /// identity again from the Grimoire's own row, the next start runs, and the work that compares the
    /// identity with that row (a replacement restore, a Covenant reset) is admitted afterwards.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_beside_an_existing_Grimoire_and_a_closed_Covenant_reset_anchor_refuses_every_host_start_and_the_repair_seeds_it_again_from_the_row()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        await InstallPriorGrimoireAsync(provider);

        Guid installation = await ReadDatabaseIdentityAsync(provider);

        // A standalone Covenant reset over the Grimoire keeps the database and its row, and leaves the
        // closed anchor of its transition journal naming the row's installation, with the profile
        // identity seeded from that same row.
        await OpenAndRetireACovenantResetAsync(installation);

        GrimoireOfflineTransitionAnchorV1 tombstone = TransitionAnchor();

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, tombstone.State);

        Assert.Equal(installation, tombstone.InstallationId);

        Assert.Equal(installation, Guid.Parse(RetainedIdentity()!));

        Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile));

        DamageTheIdentity();

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider, grimoireExists: true);

        InstallationIdentityRepairReport repaired = await RepairAsync(
            provider,
            InstallationIdentityRepairAction.Reseed);

        Assert.Equal(InstallationIdentityRepairState.Reseeded, repaired.State);

        Assert.Equal(installation, Guid.Parse(RetainedIdentity()!));

        await StartAndStopTheHostAsync(provider);

        // The start ran over the Grimoire it has always had, under the identity it always carried.
        Assert.Equal(installation, await ReadDatabaseIdentityAsync(provider));

        // A later replacement restore compares the identity with the row and is admitted, and so is the
        // opening of a later Covenant reset's transition journal beside the kept anchors.
        await ReplacementRestoreAsync(new Rebirth(provider, installation, installation, installation));

        await AssertATransitionOpensAsync(installation);
    }

    /// <summary>
    /// A closed anchor that names another installation than the Grimoire's row is a genuine conflict:
    /// the repair refuses with the mismatch, changes nothing, and the start keeps refusing.
    /// </summary>
    [SkippableFact]
    public async Task A_closed_transition_anchor_naming_another_installation_than_the_Grimoire_refuses_the_repair_and_changes_nothing()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        await InstallPriorGrimoireAsync(provider);

        Guid installation = await ReadDatabaseIdentityAsync(provider);

        Guid other = Guid.Parse("8d3f2e4a-9f74-4b2c-9c65-4e5f6a7b8c9d");

        Assert.NotEqual(installation, other);

        await OpenAndRetireACovenantResetAsync(other);

        Assert.Equal(other, TransitionAnchor().InstallationId);

        DamageTheIdentity();

        string database = DatabaseDigest();

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider, grimoireExists: true);

        await AssertTheRepairRefusesTheMismatchAsync(
            provider,
            "The closed offline-transition anchor of this profile names");

        // The anchor, the Grimoire and the item are as they were, and the start is refused still.
        GrimoireOfflineTransitionAnchorV1 anchor = TransitionAnchor();

        Assert.Equal(GrimoireOfflineTransitionAnchorState.Closed, anchor.State);

        Assert.Equal(other, anchor.InstallationId);

        Assert.Equal(database, DatabaseDigest());

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider, grimoireExists: true);
    }

    /// <summary>
    /// The same conflict through the restore journal's anchor. No start reads the identity beside a
    /// closed restore anchor once the Grimoire has its row, so the repair is the only thing that
    /// speaks, and it refuses.
    /// </summary>
    [SkippableFact]
    public async Task A_closed_restore_anchor_naming_another_installation_than_the_Grimoire_refuses_the_repair_and_changes_nothing()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        await InstallPriorGrimoireAsync(provider);

        Guid installation = await ReadDatabaseIdentityAsync(provider);

        Guid other = Guid.Parse("9e4a3f5b-8e63-4a1b-8b54-5f6a7b8c9d0e");

        Assert.NotEqual(installation, other);

        WriteAClosedRestoreAnchor(other);

        DamageTheIdentity();

        string database = DatabaseDigest();

        await AssertTheRepairRefusesTheMismatchAsync(
            provider,
            "The closed restore anchor of this profile names");

        Assert.Equal(database, DatabaseDigest());

        // The start of a Grimoire that holds its row reads the item at neither point, so it runs, and it
        // leaves the damaged item as it found it.
        await StartAndStopTheHostAsync(provider);

        Assert.Equal(DamagedIdentity, RetainedIdentity());
    }

    /// <summary>
    /// A profile with no Grimoire and no evidence of finished work is still put right by removing the
    /// item: every start is refused naming the command, the command removes it, and the next start
    /// creates a Grimoire that mints its own identity and writes none.
    /// </summary>
    [SkippableFact]
    public async Task A_damaged_identity_with_no_Grimoire_and_no_anchor_refuses_every_host_start_and_the_repair_removes_it()
    {
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

        ServiceProvider provider = CreateProvider();

        _providers.Add(provider);

        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        DamageTheIdentity();

        await AssertEveryStartIsRefusedNamingTheRepairAsync(provider);

        InstallationIdentityRepairReport repaired = await RepairAsync(
            provider,
            InstallationIdentityRepairAction.Remove);

        Assert.Equal(InstallationIdentityRepairState.Removed, repaired.State);

        Assert.Null(RetainedIdentity());

        await StartAndStopTheHostAsync(provider);

        // A start never writes the binding: the new Grimoire minted its own identity and left the
        // profile's item absent.
        Assert.Null(RetainedIdentity());
    }

    private async Task AssertTheRepairRefusesTheMismatchAsync(ServiceProvider provider, string subject)
    {
        foreach (bool apply in new[] { false, true })
        {
            Result<InstallationIdentityRepairReport> refused = await RepairResultAsync(provider, apply);

            Assert.True(refused.IsFailure, "The repair should have been refused.");

            Assert.Equal(ErrorCodes.Covenant.ManualRecoveryRequired, refused.Error.Code);

            Assert.Contains(subject, refused.Error.Message, StringComparison.Ordinal);

            Assert.Contains(
                "a different installation than the Grimoire's authority row carries",
                refused.Error.Message,
                StringComparison.Ordinal);

            Assert.Contains("Nothing was changed.", refused.Error.Message, StringComparison.Ordinal);

            Assert.Equal(DamagedIdentity, RetainedIdentity());
        }
    }

    private static string DatabaseDigest() =>
        Convert.ToHexString(
            global::System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(ArcanumPaths.GrimoireDatabaseFile)));

    private async Task AssertEveryStartIsRefusedNamingTheRepairAsync(
        ServiceProvider provider,
        bool grimoireExists = false)
    {
        string? database = grimoireExists ? DatabaseDigest() : null;

        // More than once: the refusal is repeated by every start and changes nothing.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            GrimoireDatabaseUnavailableException refused = await AssertHostRefusesAsync(provider);

            AssertNamesTheRepair(refused);

            Assert.Equal(grimoireExists, File.Exists(ArcanumPaths.GrimoireDatabaseFile));

            Assert.Equal(DamagedIdentity, RetainedIdentity());

            if (database is not null)
            {
                Assert.Equal(database, DatabaseDigest());
            }
        }
    }

    private async Task<GrimoireDatabaseUnavailableException> AssertHostRefusesAsync(ServiceProvider provider)
    {
        InvalidOperationException refused = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => CreateHost(provider).StartAsync(CancellationToken.None));

        // The message first, so a refusal in the wrong words reports what it said. Then the type: the
        // console shows the message of this one only, and "an unexpected CLI error" for any other.
        AssertNamesTheRepair(refused);

        return Assert.IsType<GrimoireDatabaseUnavailableException>(refused);
    }

    private static void AssertNamesTheRepair(Exception refused)
    {
        Assert.Contains("not one canonical identity", refused.Message, StringComparison.Ordinal);

        // True whether the profile has a Grimoire or not: neither created nor opened beside the item.
        Assert.Contains(
            "so Arcanum will not start over it. It creates no Grimoire beside it, which would replace the "
            + "binding it holds, and it opens no existing Grimoire whose installation that binding pins. "
            + "Nothing was created, and the item is left as it is.",
            refused.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain("will not create a Grimoire that would replace it", refused.Message, StringComparison.Ordinal);

        Assert.Contains(
            "Run 'arcanum data repair-installation-identity' to put the damaged item right, which it does only "
            + "when no restore, offline transition or installation reset is unfinished, and start again",
            refused.Message,
            StringComparison.Ordinal);

        Assert.DoesNotContain("Unlock or repair", refused.Message, StringComparison.Ordinal);
    }

    private async Task StartAndStopTheHostAsync(ServiceProvider provider)
    {
        GrimoireDatabaseHostedService host = CreateHost(provider);

        await host.StartAsync(CancellationToken.None);

        try
        {
            Assert.True(File.Exists(ArcanumPaths.GrimoireDatabaseFile));
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);

            host.Dispose();
        }
    }

    /// <summary>
    /// The production host over the production recovery chain: the reset record, the offline-transition
    /// journal and the restore journal, read from the same credential store the identity lives in.
    /// </summary>
    private GrimoireDatabaseHostedService CreateHost(ServiceProvider provider)
    {
        ISecretStore secrets = provider.GetRequiredService<ISecretStore>();

        secrets.SaveApiKeyAsync(HostStartApiKey).GetAwaiter().GetResult();

        ServiceCollection services = new();

        services.AddSingleton<GrimoireDbReadiness>();

        services.AddSingleton<IGrimoireDbReadiness>(
            static sp => sp.GetRequiredService<GrimoireDbReadiness>());

        services.AddSingleton<IOsCredentialStore>(_credentials);

        _ = services.AddGrimoireSchemaInstallation();

        ServiceProvider scopes = services.BuildServiceProvider();

        _providers.Add(scopes);

        return new GrimoireDatabaseHostedService(
            scopes.GetRequiredService<IServiceScopeFactory>(),
            secrets,
            new GrimoireDbPassphraseSource(),
            ArcanumPaths.GrimoireDirectory,
            new InstallationResetMaintenanceLockAccessor(),
            provider.GetRequiredService<IInstallationResetStartupRecovery>(),
            apiAdmission: null,
            startupCoordination: null,
            transitionRecovery: provider.GetRequiredService<IGrimoireOfflineTransitionStartupRecovery>());
    }

    private Task OpenAndRetireACovenantResetAsync(Guid installation) =>
        OpenACovenantResetAsync(installation, retire: true);

    /// <summary>
    /// A Covenant reset's journal for an installation, opened and optionally retired through the
    /// journal store over the real anchor, key and identity stores: the identity seeded from the
    /// installation, then the journal begun bound to it, as the phase authority does.
    /// </summary>
    private async Task OpenACovenantResetAsync(Guid installation, bool retire)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        ArcanumMaintenanceLockAcquisitionResult acquired =
            ArcanumMaintenanceLock.AcquireDetailed(ArcanumPaths.GrimoireDirectory);

        using ArcanumMaintenanceLock held = acquired.BorrowAcquiredLock();

        GrimoireOfflineTransitionJournalLocation location = new GrimoireOfflineTransitionJournalFileStore()
            .ResolveLocation(ArcanumPaths.GrimoireDirectory)
            .Value;

        Result<Guid> seeded = new BackupRestoreJournalInstallationIdentityProvider(_credentials)
            .SeedFromDatabase(held, ArcanumPaths.GrimoireDirectory, location.ProfileNamespace, installation);

        Assert.True(seeded.IsSuccess, seeded.IsFailure ? seeded.Error.Message : string.Empty);

        GrimoireOfflineTransitionJournalStore journal = new(_credentials);

        Result<GrimoireOfflineTransitionJournalPublication> begun = await journal.BeginAsync(
            held,
            ArcanumPaths.GrimoireDirectory,
            installation,
            Guid.NewGuid(),
            GrimoireOfflineTransitionKind.CovenantReset,
            payloadVersion: 1,
            Encoding.UTF8.GetBytes("covenant-reset"),
            CancellationToken.None);

        Assert.True(begun.IsSuccess, begun.IsFailure ? begun.Error.Message : string.Empty);

        if (!retire)
        {
            return;
        }

        Result retired = await journal.RetireAsync(held, begun.Value, CancellationToken.None);

        Assert.True(retired.IsSuccess, retired.IsFailure ? retired.Error.Message : string.Empty);
    }

    /// <summary>
    /// The anchor a finished restore keeps, written in the form the store writes it.
    /// </summary>
    private void WriteAClosedRestoreAnchor(Guid installation)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        BackupRestoreProfileNamespace profile = BackupRestoreJournalAuthenticator
            .ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory)
            .Value;

        BackupRestoreJournalAnchorV1 anchor = new(
            Version: 1,
            profile.Digest,
            installation,
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Revision: 3,
            new CovenantDigest([.. Enumerable.Repeat((byte)0x11, 32)]),
            new CovenantDigest([.. Enumerable.Repeat((byte)0x22, 32)]),
            BackupRestoreJournalAnchorState.Closed);

        Result<string> encoded = BackupRestoreJournalAuthenticator.EncodeAnchor(anchor);

        Assert.True(encoded.IsSuccess, encoded.IsFailure ? encoded.Error.Message : string.Empty);

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            _credentials.Set(
                ArcanumCredentialIdentity.Service,
                ArcanumCredentialIdentity.BackupRestoreJournalAnchorAccount(profile.AccountSuffix),
                encoded.Value).Status);
    }

    private static string IdentityAccount()
    {
        BackupRestoreProfileNamespace profile = BackupRestoreJournalAuthenticator
            .ResolveProfileNamespace(ArcanumPaths.GrimoireDirectory)
            .Value;

        return ArcanumCredentialIdentity.BackupRestoreJournalInstallationAccount(profile.AccountSuffix);
    }
}
