using System.Buffers.Text;
using System.Security.Cryptography;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
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

    private const string NewProfileRootUndeterminedWarning =
        "Whether this installation holds an erasure key could not be determined; its erasure evidence is not applied "
        + "to a new profile root, so items erased here can reappear there.";

    /// <summary>An item in the key's slot that is not canonical key material.</summary>
    private const string NotAKey = "not-a-key";

    private const string Keeper = "Vault Keeper";

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

    /// <summary>
    /// A keychain that cannot answer, or whose key slot holds something that is not a key, cannot prove
    /// rows the Grimoire holds: both refuse as unavailable, never as "nothing was erased".
    /// </summary>
    /// <param name="keychain">The credential store fails every call, or the key's item is not a key.</param>
    [SkippableTheory]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    public async Task An_erased_destination_with_an_unavailable_keychain_refuses_as_key_unavailable(string keychain)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        BackupRestorePlan plan = await PlanAsync(harness, archive, Keychain(harness, keychain));

        Assert.Equal("backup.restore_erasure_key_unavailable", Assert.Single(plan.Blockers).Code);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 1, 0, 0, 1), plan.DestinationErasureEvidence);
    }

    /// <summary>
    /// Every row must be recorded by the current key, not just some. An erase prepares against foreign
    /// fingerprints in its own store only, so after the key is replaced a Lexicon erase records under the
    /// new key beside a Saga fingerprint the old key recorded. The new key proves the Lexicon row and
    /// cannot prove the Saga one, so the destination cannot prove what it erased.
    /// </summary>
    [SkippableFact]
    public async Task Rows_recorded_partly_under_a_replaced_key_refuse_as_key_missing()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, NewKey()).Status);

        harness.StartHost();

        await ScribeAsync(harness.Host, Keeper);

        _ = await new MemoryErasureRouteDriver(harness.Host.CreateClient()).EraseLexiconAsync(Keeper, null);

        await harness.StopHostAsync();

        BackupRestorePlan plan = await PlanAsync(harness, archive, new CountingOsCredentialStore(harness.Credentials));

        Assert.Equal("backup.restore_erasure_key_missing", Assert.Single(plan.Blockers).Code);

        Assert.Equal(new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 1, 1, 0, 2), plan.DestinationErasureEvidence);
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
    /// <param name="absent">The database is moved aside, or else its secret is withheld.</param>
    /// <param name="key">The key is present, the credential store fails every call, or the key's item is not a key.</param>
    [SkippableTheory]
    [InlineData(true, "present")]
    [InlineData(true, "unavailable")]
    [InlineData(true, "malformed")]
    [InlineData(false, "present")]
    [InlineData(false, "unavailable")]
    [InlineData(false, "malformed")]
    public async Task A_missing_or_unreadable_destination_with_a_present_or_unavailable_key_refuses_as_evidence_unavailable(
        bool absent,
        string key)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        BackupRestorePlan plan = await PlanWithUnanswerableGrimoireAsync(harness, archive, Keychain(harness, key), absent);

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

        // The result reports what the execute-time read found, not what the plan had proven before it.
        Assert.Equal(
            new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Refused, 0, 0, 0, 0),
            result.Plan.DestinationErasureEvidence);

        Assert.DoesNotContain(result.Phases, static p => p.Phase == BackupRestorePhase.Stage);

        File.Move(aside, sidecar);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(harness.InstallationRoot)!, ".arcanum-restore-*"));
    }

    /// <summary>
    /// Every refusal names a way out that can work in the state it describes, and none carries content.
    /// </summary>
    /// <remarks>
    /// <para>A key that is missing, replaced or unreadable leaves three ways out: make the key readable and
    /// retry, reset the erasure key, or reset the installation.</para>
    ///
    /// <para>An item that is not a key needs one more step before a reset can run, because the reset
    /// refuses such an item rather than overwrite it. The message keeps the order that cannot cost a valid
    /// key: unlock and retry first, and remove the item with the OS credential tool only if it is
    /// confirmed invalid, knowing that removal makes every fingerprint unverifiable.</para>
    ///
    /// <para>An unreadable Grimoire cannot be reset through the host, which cannot open it, so that refusal
    /// offers only what can work: make the Grimoire readable and retry, or reset the installation.</para>
    /// </remarks>
    [SkippableFact]
    public async Task Every_erasure_refusal_names_its_ways_out_and_carries_no_content()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string original = harness.Credentials.TryGet(Service, Account).Value!;

        string replacement = NewKey();

        Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, replacement).Status);

        BackupVerifyIssue missing = ErasureRefusal(
            await PlanAsync(harness, archive, harness.Credentials),
            "backup.restore_erasure_key_missing");

        Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, original).Status);

        BackupVerifyIssue unavailable = ErasureRefusal(
            await PlanAsync(harness, archive, Keychain(harness, "unavailable")),
            "backup.restore_erasure_key_unavailable");

        BackupVerifyIssue unreadable = ErasureRefusal(
            await PlanWithUnanswerableGrimoireAsync(harness, archive, harness.Credentials, absent: false),
            "backup.restore_erasure_evidence_unavailable");

        BackupVerifyIssue malformed = ErasureRefusal(
            await PlanAsync(harness, archive, Keychain(harness, "malformed")),
            "backup.restore_erasure_key_unavailable");

        string[] forbidden =
        [
            Erased,
            Account,
            NotAKey,
            .. KeyIdSpellings(original),
            .. KeyIdSpellings(replacement),
        ];

        BackupVerifyIssue[] every = [missing, unavailable, unreadable, malformed];

        BackupVerifyIssue[] resettable = [missing, unavailable, malformed];

        Assert.All(every, refusal =>
        {
            Assert.Contains("retry", refusal.Message, StringComparison.Ordinal);

            Assert.Contains("full installation reset", refusal.Message, StringComparison.Ordinal);

            Assert.All(forbidden, text =>
            {
                Assert.DoesNotContain(text, refusal.Message, StringComparison.OrdinalIgnoreCase);

                Assert.DoesNotContain(text, refusal.Path ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            });
        });

        Assert.All(resettable, refusal =>
            Assert.Contains("'arcanum memory erasure reset-key' on this installation", refusal.Message, StringComparison.Ordinal));

        Assert.DoesNotContain("reset-key", unreadable.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("OS credential tool", unavailable.Message, StringComparison.Ordinal);

        Assert.Contains("unlock it and retry", malformed.Message, StringComparison.Ordinal);

        Assert.Contains("remove it with the OS credential tool", malformed.Message, StringComparison.Ordinal);

        Assert.Contains("makes every erasure fingerprint unverifiable", malformed.Message, StringComparison.Ordinal);

        Assert.True(
            malformed.Message.IndexOf("unlock it and retry", StringComparison.Ordinal)
            < malformed.Message.IndexOf("remove it with the OS credential tool", StringComparison.Ordinal),
            "Unlocking and retrying comes before removing the item, so a locked store never costs a valid key.");
    }

    /// <summary>
    /// The erasure key is asked for only once the handle that read the destination's evidence is closed,
    /// so no keychain call, which can sit behind a prompt, ever runs inside that snapshot.
    /// </summary>
    [SkippableFact]
    public async Task The_erasure_key_is_read_only_after_the_destination_handle_is_closed()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        bool handleOpen = false;

        int handlesOpened = 0;

        HandleWatchingCredentialStore keychain = new(harness.Credentials, () => handleOpen);

        BackupRestoreService service = harness.CreateRestoreService(
            keychain,
            options: new BackupRestoreServiceOptions
            {
                DestinationEvidenceHandleForTests = open =>
                {
                    handleOpen = open;

                    handlesOpened += open ? 1 : 0;
                },
            });

        BackupRestorePlan plan = await service.PlanAsync(ReplaceRequest(archive), MemoryErasureRestoreHarness.Passphrase.AsMemory(), Token);

        Assert.Equal(BackupRestoreErasureEvidenceStatus.Present, plan.DestinationErasureEvidence?.Status);

        Assert.Equal(1, handlesOpened);

        Assert.False(handleOpen);

        Assert.True(keychain.Calls > 0, "The rows were proven by the key, so the keychain was asked.");

        Assert.Equal(0, keychain.CallsWhileHandleOpen);
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

        BackupRestorePlan before = await PlanNewProfileRootAsync(harness, archive, newRoot, harness.Credentials);

        Assert.DoesNotContain(NewProfileRootWarning, before.Warnings);

        Assert.DoesNotContain(NewProfileRootUndeterminedWarning, before.Warnings);

        harness.StartHost();

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();

        BackupRestorePlan after = await PlanNewProfileRootAsync(harness, archive, newRoot, harness.Credentials);

        Assert.Contains(NewProfileRootWarning, after.Warnings);

        Assert.DoesNotContain(NewProfileRootUndeterminedWarning, after.Warnings);

        Assert.Null(after.DestinationErasureEvidence);
    }

    /// <summary>
    /// A keychain that cannot answer, or whose key slot holds something that is not a key, cannot say
    /// whether this installation erased anything, and a new-profile-root plan says so rather than nothing.
    /// </summary>
    /// <param name="keychain">The credential store fails every call, or the key's item is not a key.</param>
    [SkippableTheory]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    public async Task A_new_profile_root_plan_says_so_when_the_keychain_cannot_tell_whether_an_erasure_key_exists(string keychain)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveOneMemoryAsync(harness);

        BackupRestorePlan plan = await PlanNewProfileRootAsync(
            harness,
            archive,
            Path.Combine(harness.Profile.TempHome, "new-profile-root"),
            Keychain(harness, keychain));

        Assert.Contains(NewProfileRootUndeterminedWarning, plan.Warnings);

        Assert.DoesNotContain(NewProfileRootWarning, plan.Warnings);

        Assert.Null(plan.DestinationErasureEvidence);
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
        string destinationRoot,
        IOsCredentialStore keychain) =>
        harness
            .CreateRestoreService(keychain)
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

    /// <summary>
    /// The keychain a restore reads: the profile's own, one that fails every call, or the profile's own
    /// after the key's item was overwritten with something that is not a key.
    /// </summary>
    private static IOsCredentialStore Keychain(MemoryErasureRestoreHarness harness, string state)
    {
        switch (state)
        {
            case "present":
                return harness.Credentials;
            case "unavailable":
                return new CountingOsCredentialStore(harness.Credentials) { FailWith = OsCredentialStoreStatus.Unavailable };
            case "malformed":
                Assert.Equal(OsCredentialStoreStatus.Ok, harness.Credentials.Set(Service, Account, NotAKey).Status);

                return harness.Credentials;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Not a keychain state this suite builds.");
        }
    }

    /// <summary>Writes one Global Lexicon entry through the service's upsert, and requires that it landed.</summary>
    private static async Task ScribeAsync(ArcanumWebApplicationFactory factory, string name)
    {
        using IServiceScope scope = factory.Services.CreateScope();

        Result<LexiconEntryDto> scribed = await scope.ServiceProvider
            .GetRequiredService<ILexiconService>()
            .UpsertAsync(name, "Person", ["keeps the vault key"], LexiconScope.Global, Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);
    }

    /// <summary>Thirty-two random bytes as canonical unpadded base64url: a key, just not this one.</summary>
    private static string NewKey() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A stored key's identifier, in upper- and lower-case hex.</summary>
    private static string[] KeyIdSpellings(string storedKey)
    {
        string hex = Convert.ToHexString(MemoryErasureDigestGrammar.KeyId(Base64Url.DecodeFromChars(storedKey)));

        return [hex, hex.ToLowerInvariant()];
    }

    /// <summary>
    /// Counts every credential call, and every one made while the destination's evidence handle was open.
    /// </summary>
    private sealed class HandleWatchingCredentialStore(IOsCredentialStore inner, Func<bool> handleOpen) : IOsCredentialStore
    {
        internal int Calls { get; private set; }

        internal int CallsWhileHandleOpen { get; private set; }

        public bool IsAvailable
        {
            get
            {
                Watch();

                return inner.IsAvailable;
            }
        }

        public OsCredentialStoreResult TryGet(string service, string account)
        {
            Watch();

            return inner.TryGet(service, account);
        }

        public OsCredentialStoreResult Set(string service, string account, string secret)
        {
            Watch();

            return inner.Set(service, account, secret);
        }

        public OsCredentialStoreResult Delete(string service, string account)
        {
            Watch();

            return inner.Delete(service, account);
        }

        private void Watch()
        {
            Calls++;

            CallsWhileHandleOpen += handleOpen() ? 1 : 0;
        }
    }
}
