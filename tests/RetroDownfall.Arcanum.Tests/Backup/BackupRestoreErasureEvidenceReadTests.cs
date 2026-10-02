using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.DependencyInjection;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// A restore reads the destination's erasure evidence before anything else, from the Grimoire first and
/// with the OS key as the anchor, and refuses rather than proceed as if there were none.
/// </summary>
/// <remarks>
/// <para>Every case erases through the host's routes on a real profile, archives through the production
/// backup service, and then plans or restores through the production restore service over the same
/// in-memory keychain. "The keychain" in a case is a counting wrapper around that one store, so a case
/// can prove a destination with no evidence was planned without a single credential call.</para>
///
/// <para>The Grimoire is asked first because it is the cheap, prompt-free question. The key is the anchor
/// because it is what the Grimoire cannot fake: a destination whose rows were written under a key that is
/// gone or replaced has erasures it can no longer prove, and one whose Grimoire cannot be read while its
/// key exists may have erasures nobody can see. Both refuse; neither is read as "nothing was erased".</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class BackupRestoreErasureEvidenceReadTests
{
    private const string Erased = "erased on purpose";

    private const string Service = ArcanumCredentialIdentity.Service;

    private const string Account = ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount;

    private const string NewProfileRootWarning =
        "This installation's erasure evidence is not applied to a new profile root; items erased here can reappear there.";

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task A_destination_that_never_erased_plans_none_without_consulting_the_keychain()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveOneMemoryAsync(harness);

        CountingOsCredentialStore keychain = new(harness.Credentials);

        BackupRestorePlan plan = await PlanAsync(harness, archive, keychain);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.None, 0, 0, 0, 0), plan.DestinationErasureEvidence);

        Assert.DoesNotContain(plan.Blockers, static b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal));

        Assert.Equal(0, keychain.Calls);
    }

    [SkippableFact]
    public async Task An_erased_destination_with_its_key_plans_present_with_per_store_counts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        BackupRestorePlan plan = await PlanAsync(harness, archive, new CountingOsCredentialStore(harness.Credentials));

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Present, 1, 0, 0, 1), plan.DestinationErasureEvidence);

        Assert.Empty(plan.Blockers);
    }

    [SkippableTheory]
    [InlineData("replaced")]
    [InlineData("deleted")]
    public async Task An_erased_destination_whose_key_is_replaced_or_gone_refuses_as_key_missing(string change)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        OsCredentialStoreResult changed = change is "replaced"
            ? harness.Credentials.Set(Service, Account, NewKey())
            : harness.Credentials.Delete(Service, Account);

        Assert.Equal(OsCredentialStoreStatus.Ok, changed.Status);

        BackupRestorePlan plan = await PlanAsync(harness, archive, new CountingOsCredentialStore(harness.Credentials));

        Assert.Equal("backup.restore_erasure_key_missing", Assert.Single(plan.Blockers).Code);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 1, 0, 0, 1), plan.DestinationErasureEvidence);
    }

    [SkippableFact]
    public async Task An_erased_destination_with_an_unavailable_keychain_refuses_as_key_unavailable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        CountingOsCredentialStore keychain = new(harness.Credentials)
        {
            FailWith = OsCredentialStoreStatus.Unavailable,
        };

        BackupRestorePlan plan = await PlanAsync(harness, archive, keychain);

        Assert.Equal("backup.restore_erasure_key_unavailable", Assert.Single(plan.Blockers).Code);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 1, 0, 0, 1), plan.DestinationErasureEvidence);
    }

    /// <summary>
    /// A receipt is evidence on its own. Release removes an erased item's fingerprint and keeps the record
    /// of its erasure, so a destination holding only receipts is still proven by its key, every receipt's
    /// key included.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_receipt_kept_after_its_fingerprint_was_released_is_still_evidence(bool keyReplaced)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        harness.StartHost();

        MemoryErasureReleaseResultDto released = await new MemoryErasureRouteDriver(harness.Host.CreateClient())
            .ReleaseSagaAsync(new(SagaMemoryScopeKind.Global, null, Erased));

        Assert.Equal(new MemoryErasureReleaseResultDto(MemoryReviewStore.Saga, MemoryErasureReleaseOutcome.Released, 1), released);

        await harness.StopHostAsync();

        if (keyReplaced)
        {
            Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, NewKey()).Status);
        }

        BackupRestorePlan plan = await PlanAsync(harness, archive, new CountingOsCredentialStore(harness.Credentials));

        Assert.Equal(
            new BackupRestoreErasureEvidenceSummary(
                keyReplaced ? BackupRestoreErasureEvidenceStatus.Refused : BackupRestoreErasureEvidenceStatus.Present,
                0,
                0,
                0,
                1),
            plan.DestinationErasureEvidence);

        string[] blockers = keyReplaced ? ["backup.restore_erasure_key_missing"] : [];

        Assert.Equal(blockers, plan.Blockers.Select(static b => b.Code));
    }

    /// <summary>
    /// A Grimoire that cannot answer leaves the key to: with no key, nothing can have been committed under
    /// one, so there is no evidence to lose.
    /// </summary>
    /// <param name="absent">The database is moved aside, or else its secret is withheld.</param>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_missing_or_unreadable_destination_without_a_key_plans_none(bool absent)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Delete(Service, Account).Status);

        CountingOsCredentialStore keychain = new(harness.Credentials);

        BackupRestorePlan plan = await PlanWithUnanswerableGrimoireAsync(harness, archive, keychain, absent);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.None, 0, 0, 0, 0), plan.DestinationErasureEvidence);

        Assert.DoesNotContain(plan.Blockers, static b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal));

        // The Grimoire could not answer, so the key was asked once.
        Assert.Equal(1, keychain.Calls);
    }

    /// <summary>
    /// A Grimoire that cannot answer while a key exists, or while the keychain cannot say whether one does,
    /// may hold erasures nobody can see, so the restore refuses rather than overwrite them.
    /// </summary>
    [SkippableTheory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task A_missing_or_unreadable_destination_with_a_present_or_unavailable_key_refuses_as_evidence_unavailable(
        bool absent,
        bool keychainUnavailable)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        CountingOsCredentialStore keychain = new(harness.Credentials)
        {
            FailWith = keychainUnavailable ? OsCredentialStoreStatus.Unavailable : null,
        };

        BackupRestorePlan plan = await PlanWithUnanswerableGrimoireAsync(harness, archive, keychain, absent);

        Assert.Equal(
            "backup.restore_erasure_evidence_unavailable",
            Assert.Single(plan.Blockers, static b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal)).Code);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 0, 0, 0, 0), plan.DestinationErasureEvidence);
    }

    /// <summary>
    /// The plan read is not the last word: evidence that becomes unreadable between plan and execute is
    /// caught by the re-read at Stage, before any extraction directory exists.
    /// </summary>
    /// <remarks>
    /// A Present latch is never re-probed, so the execute-time change has to be the Grimoire's
    /// readability rather than the keychain's.
    /// </remarks>
    [SkippableFact]
    public async Task A_refusal_first_seen_at_execute_stops_before_extraction()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string before = await harness.LiveDatabaseDigestAsync();

        string sidecar = harness.DatabasePath + ".kdf";

        string aside = sidecar + ".aside";

        BackupRestoreService service = harness.CreateRestoreService(
            options: new BackupRestoreServiceOptions
            {
                BeforePhaseForTests = phase =>
                {
                    if (phase == BackupRestorePhase.Stage)
                    {
                        File.Move(sidecar, aside);
                    }
                },
            });

        BackupRestorePlan plan = await service.PlanAsync(ReplaceRequest(archive), MemoryErasureRestoreHarness.Passphrase.AsMemory(), Token);

        Assert.Equal(BackupRestoreErasureEvidenceStatus.Present, plan.DestinationErasureEvidence?.Status);

        BackupRestoreResult result = await service.RestoreAsync(
            ReplaceRequest(archive) with { Confirmed = true },
            MemoryErasureRestoreHarness.Passphrase.AsMemory(),
            Token);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Equal("backup.restore_erasure_evidence_unavailable", Assert.Single(result.Issues).Code);

        Assert.DoesNotContain(result.Phases, static p => p.Phase == BackupRestorePhase.Stage);

        File.Move(aside, sidecar);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(harness.InstallationRoot)!, ".arcanum-restore-*"));
    }

    [SkippableFact]
    public async Task Every_erasure_refusal_names_its_ways_out_and_carries_no_content()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string original = harness.Credentials.TryGet(Service, Account).Value!;

        string replacement = NewKey();

        List<BackupVerifyIssue> refusals = [];

        Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, replacement).Status);

        refusals.Add(ErasureRefusal(await PlanAsync(harness, archive, harness.Credentials), "backup.restore_erasure_key_missing"));

        Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, original).Status);

        refusals.Add(ErasureRefusal(
            await PlanAsync(harness, archive, new CountingOsCredentialStore(harness.Credentials) { FailWith = OsCredentialStoreStatus.Unavailable }),
            "backup.restore_erasure_key_unavailable"));

        refusals.Add(ErasureRefusal(
            await PlanWithUnanswerableGrimoireAsync(harness, archive, harness.Credentials, absent: false),
            "backup.restore_erasure_evidence_unavailable"));

        string[] forbidden =
        [
            Erased,
            Account,
            .. KeyIdSpellings(original),
            .. KeyIdSpellings(replacement),
        ];

        Assert.All(refusals, refusal =>
        {
            Assert.Contains("retry", refusal.Message, StringComparison.Ordinal);

            Assert.Contains("arcanum memory erasure reset-key", refusal.Message, StringComparison.Ordinal);

            Assert.Contains("full installation reset", refusal.Message, StringComparison.Ordinal);

            Assert.All(forbidden, text =>
            {
                Assert.DoesNotContain(text, refusal.Message, StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(text, refusal.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            });
        });
    }

    [SkippableFact]
    public async Task A_new_profile_root_plan_warns_when_this_installation_holds_an_erasure_key()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string memory = await harness.InsertSagaAsync(Erased);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("new-profile-root.arcbackup");

        string newRoot = Path.Combine(harness.Profile.TempHome, "new-profile-root");

        BackupRestorePlan before = await PlanNewProfileRootAsync(harness, archive, newRoot);

        Assert.DoesNotContain(NewProfileRootWarning, before.Warnings);

        harness.StartHost();

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();

        BackupRestorePlan after = await PlanNewProfileRootAsync(harness, archive, newRoot);

        Assert.Contains(NewProfileRootWarning, after.Warnings);

        Assert.Null(after.DestinationErasureEvidence);
    }

    [SkippableFact]
    public async Task A_catalog_below_version_thirteen_reads_as_no_evidence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(Token);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionTwelveFixture.ChainSet(),
            1536,
            Token);

        Assert.Equal(12, installed.Core.SchemaVersion);

        Assert.Null(await MemoryErasureEvidence.ReadSnapshotAsync(connection, null, Token));
    }

    /// <summary>
    /// The CLI's restore container holds one keyring, the same singleton the Grimoire registrations use,
    /// so a plan and the restore it confirms read one latch.
    /// </summary>
    [Fact]
    public void The_cli_client_stack_registers_exactly_one_singleton_erasure_key_provider()
    {
        ServiceCollection services = new();

        _ = services.AddArcanumCliClientStack();

        Assert.Equal(
            ServiceLifetime.Singleton,
            Assert.Single(services, static d => d.ServiceType == typeof(IMemoryErasureKeyProvider)).Lifetime);
    }

    /// <summary>Starts a host, writes one memory, and archives the installation, erasing nothing.</summary>
    private static async Task<string> ArchiveOneMemoryAsync(MemoryErasureRestoreHarness harness)
    {
        harness.StartHost();

        _ = await harness.InsertSagaAsync(Erased);

        await harness.StopHostAsync();

        return await harness.CreateArchiveAsync("never-erased.arcbackup");
    }

    /// <summary>
    /// Writes one Global memory, archives the installation with it, then erases it, which creates the
    /// key: one Saga fingerprint and one receipt, and an archive that still carries the memory.
    /// </summary>
    private static async Task<string> ArchiveThenEraseAsync(MemoryErasureRestoreHarness harness)
    {
        harness.StartHost();

        string memory = await harness.InsertSagaAsync(Erased);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("before-erase.arcbackup");

        harness.StartHost();

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();

        return archive;
    }

    private static Task<BackupRestorePlan> PlanAsync(
        MemoryErasureRestoreHarness harness,
        string archive,
        IOsCredentialStore keychain,
        ISecretStore? secrets = null) =>
        harness
            .CreateRestoreService(keychain, secrets)
            .PlanAsync(ReplaceRequest(archive), MemoryErasureRestoreHarness.Passphrase.AsMemory(), Token);

    /// <summary>Plans with the Grimoire moved aside, or with its secret withheld.</summary>
    private static async Task<BackupRestorePlan> PlanWithUnanswerableGrimoireAsync(
        MemoryErasureRestoreHarness harness,
        string archive,
        IOsCredentialStore keychain,
        bool absent)
    {
        if (!absent)
        {
            return await PlanAsync(harness, archive, keychain, new MemoryErasureRestoreHarness.FixedGrimoireSecretStore(null));
        }

        string aside = harness.DatabasePath + ".aside";

        File.Move(harness.DatabasePath, aside);

        try
        {
            return await PlanAsync(harness, archive, keychain);
        }
        finally
        {
            File.Move(aside, harness.DatabasePath);
        }
    }

    private static Task<BackupRestorePlan> PlanNewProfileRootAsync(
        MemoryErasureRestoreHarness harness,
        string archive,
        string destinationRoot) =>
        harness
            .CreateRestoreService()
            .PlanAsync(
                new BackupRestoreRequest(archive, BackupRestoreConflictMode.NewProfileRoot, destinationRoot),
                MemoryErasureRestoreHarness.Passphrase.AsMemory(),
                Token);

    private static BackupRestoreRequest ReplaceRequest(string archive) =>
        new(archive, BackupRestoreConflictMode.ReplaceInstallation, CreateSafetyBackup: false);

    private static BackupVerifyIssue ErasureRefusal(BackupRestorePlan plan, string code)
    {
        BackupVerifyIssue refusal = Assert.Single(
            plan.Blockers,
            static b => b.Code.StartsWith("backup.restore_erasure_", StringComparison.Ordinal));

        Assert.Equal(code, refusal.Code);

        return refusal;
    }

    /// <summary>Thirty-two random bytes as canonical unpadded base64url: a key, just not this one.</summary>
    private static string NewKey() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A stored key's identifier, in upper- and lower-case hex.</summary>
    private static string[] KeyIdSpellings(string storedKey)
    {
        string hex = Convert.ToHexString(MemoryErasureDigestGrammar.KeyId(Base64Url.DecodeFromChars(storedKey)));

        return [hex, hex.ToLowerInvariant()];
    }
}
