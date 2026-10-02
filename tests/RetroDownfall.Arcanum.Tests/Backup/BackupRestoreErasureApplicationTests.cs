using System.Data;
using System.Net;
using System.Net.Http.Json;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.NativeSqlCipher;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// A restore applies this installation's erasure evidence to the archive it stages: every archived item
/// this installation erased is purged from the staged copy, the evidence becomes this installation's
/// exactly, and the purge is proven before anything is committed.
/// </summary>
/// <remarks>
/// <para>Every case establishes its preconditions through the host's own writers on a real profile,
/// archives through the production backup service, erases through the routes, stops the host, and then
/// restores through the production restore service over the same in-memory keychain. Unless a case says
/// otherwise the restore replaces the installation with the Covenant gate off, and "M" is the Saga memory
/// <c>erase me</c> in Global scope.</para>
///
/// <para>A successful restore always reports the destination's evidence as the execute-time read found
/// it, so every one asserts that summary.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class BackupRestoreErasureApplicationTests
{
    private const string M = "erase me";

    private const string N = "keep me";

    private const string Vault = "preference.vault";

    private const string Harbor = "preference.harbor";

    private const string VerificationFailed = "backup.restore_erasure_verification_failed";

    private const string ProtectedStatePresent = "backup.restore_protected_state_present";

    private static readonly Guid BootId = Guid.Parse("d4d4d4d4-0000-4000-8000-0000000000aa");

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task A_restore_removes_a_saga_memory_this_installation_erased_and_keeps_its_twin_elsewhere()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        (_, Guid sessionB) = await BoundSessionAsync(harness, "b");

        string m = await harness.InsertSagaAsync(M);

        string m2 = await harness.InsertSagaAsync(M, sessionB);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("saga.arcbackup");

        harness.StartHost();

        _ = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        string destinationEvidence = await DestinationEvidenceHexAsync(harness);

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Id = $m", ("$m", m)));

            Assert.Equal(
                0L,
                await CountAsync(
                    restored,
                    "SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 1 AND lower(replace(SubjectId, '-', '')) = $m",
                    ("$m", Key(m))));

            Assert.Equal(1L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Id = $m2", ("$m2", m2)));

            Assert.Equal(destinationEvidence, await MemoryErasureRestoreHarness.EvidenceHexAsync(restored));
        }

        Assert.Equal(
            new BackupRestoreErasureApplication(1, 0, 0, 0, 1, 1, 0, BackupRestoreErasureScrubStatus.Verified),
            result.Reconciliation!.ErasureApplication);

        harness.StartHost();

        Assert.Equal(SagaMemoryWriteOutcome.Suppressed, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(harness.Host, M));
    }

    /// <summary>
    /// A memory corrected before the backup carries a chain of claim versions, most of which go by
    /// cascade. The count reported is the items the plan measured inside the transaction, and the whole
    /// chain the host's own erase measured is gone.
    /// </summary>
    [SkippableFact]
    public async Task A_corrected_memory_is_counted_once_and_its_whole_annals_chain_is_purged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string m = await harness.InsertSagaAsync("erase me, first draft");

        using (HttpResponseMessage corrected = await harness.PostAsync(
            $"/api/memory/saga/{m}/correct",
            new SagaCorrectRequest(Hash("erase me, first draft"), M),
            ArcanumJsonContext.Default.SagaCorrectRequest))
        {
            Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        }

        await harness.StopHostAsync();

        long chain;

        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            chain = await CountAsync(
                archived,
                """
                SELECT COUNT(*) FROM annal_versions
                WHERE ClaimId IN (SELECT ClaimId FROM annal_claims WHERE SubjectStoreCode = 1 AND lower(replace(SubjectId, '-', '')) = $m)
                """,
                ("$m", Key(m)));
        }

        // The precondition this case is about: more than one version, so the purge reaches a cascade.
        Assert.True(chain >= 2, $"Expected a corrected chain, found {chain} versions.");

        string archive = await harness.CreateArchiveAsync("corrected.arcbackup");

        harness.StartHost();

        MemoryErasureRoundTrip<SagaEraseRequest> erased = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        Assert.Equal(erased.Preflight.Plan.RowsToRemove, erased.Result.Local.RemovedRowCount);

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        Assert.Equal(erased.Preflight.Plan.ErasedItemCount, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Id = $m", ("$m", m)));

        Assert.Equal(
            0L,
            await CountAsync(
                restored,
                "SELECT COUNT(*) FROM annal_claims WHERE SubjectStoreCode = 1 AND lower(replace(SubjectId, '-', '')) = $m",
                ("$m", Key(m))));

        await AnnalsOrphanAssertions.AssertNoOrphanClaimsAsync(restored, Token);
    }

    [SkippableFact]
    public async Task A_restore_of_an_archive_taken_while_the_memory_was_retired_leaves_no_retirement_digest()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string m = await harness.InsertSagaAsync(M);

        using (HttpResponseMessage retired = await harness.PostAsync(
            $"/api/memory/saga/{m}/retire",
            new SagaRetireRequest(Hash(M)),
            ArcanumJsonContext.Default.SagaRetireRequest))
        {
            Assert.Equal(HttpStatusCode.OK, retired.StatusCode);
        }

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("retired.arcbackup");

        // The archive really carries the retirement pair the restore has to remove.
        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(1, await SagaRetirementSuppression.CountPairAsync(archived, null, SagaMemoryScopeKind.Global, null, M, Token));
        }

        harness.StartHost();

        _ = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(0, await SagaRetirementSuppression.CountPairAsync(restored, null, SagaMemoryScopeKind.Global, null, M, Token));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Id = $m", ("$m", m)));
        }

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.RetirementPairsRemoved);
    }

    [SkippableFact]
    public async Task A_restore_removes_an_erased_lexicon_entry_and_leaves_no_search_token()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        await ScribeAsync(harness, "Qqz Vault Sigil", null, ["qqzsigiltoken"]);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("lexicon.arcbackup");

        // The positive control both absence checks below are measured against.
        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(1L, await CountAsync(archived, "SELECT COUNT(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'qqzsigiltoken'"));

            Assert.True(await CountAsync(archived, TokenBlocksSql) > 0);
        }

        harness.StartHost();

        _ = await harness.EraseLexiconAsync("Qqz Vault Sigil", null);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 1, covenant: 0, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'qqzsigiltoken'"));

            Assert.Equal(0L, await CountAsync(restored, TokenBlocksSql));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM lexicon_entries WHERE Name = 'Qqz Vault Sigil'"));
        }

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.LexiconEntriesRemoved);
    }

    [SkippableFact]
    public async Task A_gate_off_restore_removes_an_erased_covenant_entry_and_requires_a_full_rebuild()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep the vault key offline.");

        await SynchronizeSearchAsync(harness);

        await harness.StopHostAsync();

        string entry;

        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            entry = await ScalarTextAsync(archived, "SELECT EntryId FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Vault));

            // What the archive carries: a published search projection and an applied search tuple.
            Assert.True(await CountAsync(archived, "SELECT COUNT(*) FROM covenant_search_documents WHERE NormalizedKey = $key", ("$key", Vault)) > 0);

            Assert.Equal(1L, await CountAsync(archived, "SELECT COUNT(*) FROM covenant_state WHERE AppliedDatasetGeneration IS NOT NULL"));
        }

        string archive = await harness.CreateArchiveAsync("covenant.arcbackup");

        harness.StartHost(covenant: true);

        _ = await harness.EraseCovenantAsync(CovenantScope.Global, null, Vault);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 0, covenant: 1, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Vault)));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_versions WHERE EntryId = $entry", ("$entry", entry)));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_heads WHERE EntryId = $entry", ("$entry", entry)));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_search_documents WHERE EntryId = $entry", ("$entry", entry)));

            Assert.Equal(
                1L,
                await CountAsync(
                    restored,
                    "SELECT COUNT(*) FROM covenant_state WHERE AppliedDatasetGeneration IS NULL AND AppliedSearchSequence IS NULL AND RebuildStateCode = 2"));
        }

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.CovenantEntriesRemoved);
    }

    [SkippableFact]
    public async Task Restore_protected_state_keeps_the_archive_covenant_data_except_the_erased_entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep the vault key offline.");

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Harbor, "Moor at the east harbor.");

        await harness.StopHostAsync();

        string e2;

        string e2Head;

        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            e2 = await ScalarTextAsync(archived, "SELECT EntryId FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Harbor));

            e2Head = await ScalarTextAsync(
                archived,
                "SELECT CurrentVersionId FROM covenant_heads WHERE EntryId = $e2 AND LaneCode = 1",
                ("$e2", e2));
        }

        string archive = await harness.CreateArchiveAsync("protected.arcbackup");

        harness.StartHost(covenant: true);

        _ = await harness.EraseCovenantAsync(CovenantScope.Global, null, Vault);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(
            harness.CreateRestoreService(covenantStaging: true),
            archive,
            BackupProtectedStateMode.RestoreProtectedState);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 0, covenant: 1, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Vault)));

            // Reclaimed in staging: no other entry or head in any scope still named the key.
            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_key_epochs WHERE NormalizedKey = $key", ("$key", Vault)));

            Assert.Equal(1L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Harbor)));

            Assert.Equal(
                e2Head,
                await ScalarTextAsync(restored, "SELECT CurrentVersionId FROM covenant_heads WHERE EntryId = $e2 AND LaneCode = 1", ("$e2", e2)));
        }

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.CovenantEntriesRemoved);
    }

    /// <summary>
    /// The default mode refuses an archive that carries Covenant rows before anything is staged, even
    /// when every one of them is an entry this installation erased: that is decided from the archive
    /// before the evidence step can run, and it is the conservative answer.
    /// </summary>
    [SkippableFact]
    public async Task The_default_mode_still_refuses_an_archive_whose_only_covenant_content_was_erased()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep the vault key offline.");

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("erased-only.arcbackup");

        harness.StartHost(covenant: true);

        _ = await harness.EraseCovenantAsync(CovenantScope.Global, null, Vault);

        await harness.StopHostAsync();

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(covenantStaging: true), archive);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Equal(ProtectedStatePresent, Assert.Single(result.Issues).Code);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
    }

    [SkippableFact]
    public async Task Purge_protected_state_runs_the_evidence_step_first()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        string m = await harness.InsertSagaAsync(M);

        await LabelAsync(harness, m, sessionId: null, campaignId: null, M);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("purge.arcbackup");

        harness.StartHost(covenant: true);

        _ = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(
            harness.CreateRestoreService(covenantStaging: true),
            archive,
            BackupProtectedStateMode.PurgeProtectedState);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        // Had the purge run first it would already have removed the labelled M, and the evidence step
        // would have found nothing to count.
        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);

        int evidence = Array.FindIndex(result.Phases, static p => p.Detail.StartsWith("Applied destination erasure evidence", StringComparison.Ordinal));

        int covenant = Array.FindIndex(result.Phases, static p => p.Detail.StartsWith("Stripped the archive's managed-file authority", StringComparison.Ordinal));

        Assert.InRange(evidence, 0, covenant - 1);
    }

    [SkippableFact]
    public async Task A_restore_recounts_the_owning_sessions_tainted_artifacts()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        (Guid campaign, Guid session) = await BoundSessionAsync(harness, "taint");

        string m = await harness.InsertSagaAsync(M, session);

        string n = await harness.InsertSagaAsync(N, session);

        await LabelAsync(harness, m, session, campaign, M);

        await LabelAsync(harness, n, session, campaign, N);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("taint.arcbackup");

        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(2L, await CountAsync(archived, SessionTaintSql, ("$s", Key(session))));
        }

        harness.StartHost(covenant: true);

        _ = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(
            harness.CreateRestoreService(covenantStaging: true),
            archive,
            BackupProtectedStateMode.RestoreProtectedState);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        // Recounted from the labels that remain, not folded to zero.
        Assert.Equal(1L, await CountAsync(restored, SessionTaintSql, ("$s", Key(session))));

        Assert.Equal(0L, await CountAsync(restored, LabelSql, ("$a", Key(m))));

        Assert.Equal(1L, await CountAsync(restored, LabelSql, ("$a", Key(n))));
    }

    [SkippableFact]
    public async Task An_archive_at_core_eleven_is_drained_then_purged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "v11",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            static connection => ExecuteAsync(
                connection,
                """
                INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
                VALUES ('7A1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C', 'erase me', '2026-01-01T00:00:00.0000000Z', 1);
                """));

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret))
        {
            Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Content = 'erase me' AND ScopeKindCode = 1"));
        }

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);

        int drained = Array.FindIndex(result.Phases, static p => p.Detail.StartsWith("Drained staged schema transitions", StringComparison.Ordinal));

        int applied = Array.FindIndex(result.Phases, static p => p.Detail.StartsWith("Applied destination erasure evidence", StringComparison.Ordinal));

        Assert.InRange(drained, 0, applied - 1);
    }

    [SkippableFact]
    public async Task The_destination_evidence_replaces_the_archive_evidence_and_never_revives_a_release()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        // The archive holds the fingerprint and the receipt.
        string archive = await harness.CreateArchiveAsync("released.arcbackup");

        harness.StartHost();

        Assert.Equal(
            MemoryErasureReleaseOutcome.Released,
            (await harness.ReleaseSagaAsync(new SagaErasureReleaseRequest(SagaMemoryScopeKind.Global, null, M))).Outcome);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 0, covenant: 0, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM memory_erasure_fingerprints"));

            Assert.Equal(1L, await CountAsync(restored, "SELECT COUNT(*) FROM memory_erasure_receipts"));
        }

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.ArchiveRowsDropped);

        harness.StartHost();

        Assert.Equal(SagaMemoryWriteOutcome.Written, await MemoryErasureRouteDriver.InsertSagaOutcomeAsync(harness.Host, M));
    }

    [SkippableFact]
    public async Task Archive_evidence_under_a_foreign_key_is_dropped_and_counted()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string m = await harness.InsertSagaAsync(M);

        string other = await harness.InsertSagaAsync("erase me later");

        _ = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("first-key.arcbackup");

        Assert.Equal(
            OsCredentialStoreStatus.Ok,
            harness.Credentials.Delete(ArcanumCredentialIdentity.Service, ArcanumCredentialIdentity.MemoryErasureFingerprintKeyAccount).Status);

        harness.StartHost();

        await ResetKeyAsync(harness);

        _ = await harness.EraseSagaAsync(other);

        await harness.StopHostAsync();

        string destinationEvidence = await DestinationEvidenceHexAsync(harness);

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        // The fingerprint and the receipt the first key recorded; their receipt subject is not counted.
        Assert.Equal(2, result.Reconciliation!.ErasureApplication!.ArchiveRowsDropped);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        Assert.Equal(destinationEvidence, await MemoryErasureRestoreHarness.EvidenceHexAsync(restored));

        Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Id = $m", ("$m", other)));
    }

    /// <summary>
    /// An operator <c>set</c> of an erased key releases its fingerprint and keeps the receipt, so an
    /// archive taken afterwards carries a live entry this installation has nothing against: the restore
    /// keeps it, and joins only the receipt.
    /// </summary>
    [SkippableFact]
    public async Task A_backup_taken_after_an_operator_reset_of_an_erased_key_keeps_the_re_created_entry()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep the vault key offline.");

        _ = await harness.EraseCovenantAsync(CovenantScope.Global, null, Vault);

        CovenantMutationResultDto recreated = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep it in the tower now.");

        Assert.True(recreated.ReleasedErasureFingerprint);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("recreated.arcbackup");

        BackupRestoreResult result = await RestoreAsync(
            harness.CreateRestoreService(covenantStaging: true),
            archive,
            BackupProtectedStateMode.RestoreProtectedState);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 0, covenant: 0, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.Equal(1L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Vault)));
        }

        Assert.Equal(
            new BackupRestoreErasureApplication(0, 0, 0, 0, 0, 1, 0, BackupRestoreErasureScrubStatus.Verified),
            result.Reconciliation!.ErasureApplication);
    }

    [SkippableTheory]
    [InlineData("upper-dashed")]
    [InlineData("lower-dashed")]
    [InlineData("lower-undashed")]
    public async Task A_staged_lexicon_row_in_any_campaign_spelling_matches_the_fingerprint(string spelling)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        Guid campaign = await RegisterCampaignAsync(harness, "spell");

        await ScribeAsync(harness, "Vault Keeper", campaign, ["keeps the vault key"]);

        _ = await harness.EraseLexiconAsync("Vault Keeper", campaign);

        await harness.StopHostAsync();

        string stored = spelling switch
        {
            "upper-dashed" => campaign.ToString("D").ToUpperInvariant(),
            "lower-dashed" => campaign.ToString("D"),
            _ => campaign.ToString("N"),
        };

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "spell-" + spelling,
            GrimoireSchemaVersionChains.Default,
            connection => ExecuteAsync(
                connection,
                """
                INSERT INTO lexicon_entries (Id, Name, NameNormalized, Type, FactsJson, FactsText, UpdatedAt, ScopeCampaignId)
                VALUES ('5B1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C', 'Vault Keeper', 'VAULT KEEPER', 'Person', '["keeps the vault key"]',
                        'keeps the vault key', '2026-01-01T00:00:00.0000000Z', $campaign);
                """,
                ("$campaign", stored)));

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 1, covenant: 0, receipts: 1);

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.LexiconEntriesRemoved);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM lexicon_entries WHERE Name = 'Vault Keeper'"));
    }

    /// <summary>
    /// An archive from before the identity guard can hold a Campaign in a spelling no current writer
    /// produces. The drain's version-5 sweep canonicalizes it, and the match reads the GUID bytes either
    /// way, so the memory is still found.
    /// </summary>
    [SkippableFact]
    public async Task A_core_four_archive_with_a_minority_campaign_spelling_is_drained_then_matched()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        (Guid campaign, Guid session) = await BoundSessionAsync(harness, "secret");

        string memory = await harness.InsertSagaAsync("campaign secret", session);

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "v4",
            CoreSchemaVersionFourFixture.ChainSet(),
            connection => ExecuteAsync(
                connection,
                """
                INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode, CampaignId)
                VALUES ('4C1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C', 'campaign secret', '2026-01-01T00:00:00.0000000Z', 2, $campaign);
                """,
                ("$campaign", campaign.ToString("D").ToLowerInvariant())));

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.SagaMemoriesRemoved);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM saga_memories WHERE Content = 'campaign secret'"));
    }

    [SkippableFact]
    public async Task A_verification_failure_rolls_back_staging_and_leaves_the_installation_unchanged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreService service = harness.CreateRestoreService(
            options: new BackupRestoreServiceOptions
            {
                // A row of the erased identity, under a new id, appears after the purge and before the
                // post-conditions: only the rescan of the whole staged store can see it.
                AfterErasurePurgeForTests = static async (connection, transaction, cancellationToken) =>
                {
                    await using SqliteCommand command = connection.CreateCommand();

                    command.Transaction = transaction;

                    command.CommandText = """
                        INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
                        VALUES ('3E1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C', 'erase me', '2026-01-01T00:00:00.0000000Z', 1);
                        """;

                    _ = await command.ExecuteNonQueryAsync(cancellationToken);
                },
            });

        BackupRestoreResult result = await RestoreAsync(service, archive);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(VerificationFailed, issue.Code);

        Assert.DoesNotContain(M, issue.Message, StringComparison.Ordinal);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        AssertNoStagingRemains(harness);
    }

    /// <summary>
    /// The key the evidence step fingerprints with is the one the destination read latched. A latched key
    /// that did not record the destination's evidence would match nothing and let every erased item back,
    /// so the step refuses before it begins, and nothing is committed.
    /// </summary>
    [SkippableFact]
    public async Task A_latched_key_that_did_not_record_the_evidence_refuses_before_anything_is_staged()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult result = await RestoreAsync(
            harness.CreateRestoreService(erasureKeys: new ForeignLatchedKey(new MemoryErasureKeyring(harness.Credentials))),
            archive);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        Assert.Equal("backup.restore_erasure_key_missing", Assert.Single(result.Issues).Code);

        Assert.DoesNotContain(result.Phases, static p => p.Detail.StartsWith("Applied destination erasure evidence", StringComparison.Ordinal));

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        AssertNoStagingRemains(harness);
    }

    [SkippableFact]
    public async Task A_post_commit_match_is_reported_as_reconciliation_required()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string database = harness.DatabasePath;

        BackupRestoreService service = harness.CreateRestoreService(
            options: new BackupRestoreServiceOptions
            {
                BeforePhaseForTests = phase =>
                {
                    if (phase != BackupRestorePhase.Reconcile)
                    {
                        return;
                    }

                    // The committed generation, written after the staged proof and before the
                    // post-commit one: the only window in which an erased item could reappear.
                    using SqliteConnection committed = BackupRestoreDatabaseWorker
                        .OpenAsync(database, GrimoireFixture.TestGrimoireSecret, readOnly: false, Token)
                        .GetAwaiter()
                        .GetResult();

                    using SqliteCommand command = committed.CreateCommand();

                    command.CommandText = """
                        INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
                        VALUES ('2E1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C', 'erase me', '2026-01-01T00:00:00.0000000Z', 1);
                        """;

                    _ = command.ExecuteNonQuery();
                },
            });

        BackupRestoreResult result = await RestoreAsync(service, archive);

        Assert.Equal(BackupRestoreStatus.ReconciliationRequired, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        Assert.Contains(
            "backup.restore_erasure_verification_failed: an erased item is present in the committed generation.",
            result.Reconciliation!.Issues);

        Assert.Equal(BackupRestoreErasureScrubStatus.ScrubPending, result.Reconciliation.ErasureApplication!.Scrub);
    }

    [SkippableFact]
    public async Task The_extracted_archive_database_is_deleted_before_commit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        string archive = await ArchiveThenEraseAsync(harness);

        string parent = Path.GetDirectoryName(harness.InstallationRoot)!;

        Dictionary<string, bool>? seen = null;

        BackupRestoreService service = harness.CreateRestoreService(
            options: new BackupRestoreServiceOptions
            {
                BeforePhaseForTests = phase =>
                {
                    if (phase != BackupRestorePhase.Commit)
                    {
                        return;
                    }

                    string extract = Path.Combine(
                        Assert.Single(Directory.GetDirectories(parent, BackupRestoreJournal.StagingPrefix + "*")),
                        BackupRestoreJournal.WorkDirectoryName,
                        "extract");

                    string database = Path.Combine(extract, "grimoire", "arcanum.db");

                    seen = new(StringComparer.Ordinal)
                    {
                        ["database"] = File.Exists(database),
                        ["wal"] = File.Exists(database + "-wal"),
                        ["shm"] = File.Exists(database + "-shm"),
                        // The control: the same extraction root, still holding the recovery material the
                        // commit's secret rewrap reads after this point.
                        ["recovery"] = File.Exists(Path.Combine(extract, "recovery", "portable-keys.json")),
                    };
                },
            });

        BackupRestoreResult result = await RestoreAsync(service, archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 1, lexicon: 0, covenant: 0, receipts: 1);

        Assert.NotNull(seen);

        Assert.False(seen!["database"]);

        Assert.False(seen["wal"]);

        Assert.False(seen["shm"]);

        Assert.True(seen["recovery"]);
    }

    [SkippableFact]
    public async Task A_gate_off_restore_joins_the_destination_disclosure_buckets_and_folds_staged_tails()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        await harness.StopHostAsync();

        await using (SqliteConnection destination = await BackupRestoreDatabaseWorker.OpenAsync(
            harness.DatabasePath,
            GrimoireFixture.TestGrimoireSecret,
            readOnly: false,
            Token))
        {
            CovenantDisclosureTransactionWriter writer = new(BootId);

            foreach ((byte seed, long timestamp) in ((byte, long)[])[(1, 1_700_000_000_000), (2, 1_700_000_060_000)])
            {
                Result<CovenantDisclosureReceipt> acknowledged = await writer.AcknowledgeAsync(
                    destination,
                    Draft(Guid.Parse("c3c3c3c3-0000-4000-8000-000000000003"), seed, timestamp),
                    CovenantDisclosureEffectCategory.ProviderDispatch,
                    PreFoldDisclosureHistory.Sensitivity,
                    Token);

                Assert.True(acknowledged.IsSuccess, acknowledged.IsFailure ? acknowledged.Error.Message : null);
            }

            CovenantDisclosureState exact = await ProviderBucketAsync(destination);

            Assert.Equal((CovenantDisclosureCountKind.Exact, 2ul), (exact.CountKind, exact.Count));
        }

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "disclosure",
            GrimoireSchemaVersionChains.Default,
            static connection => PreFoldDisclosureHistory.InsertAsync(
                connection,
                Draft(Guid.Parse("a1a1a1a1-0000-4000-8000-000000000001"), 7, 1_700_000_000_000),
                1,
                CancellationToken.None));

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(
            new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.None, 0, 0, 0, 0),
            result.Plan.DestinationErasureEvidence);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret))
        {
            CovenantDisclosureState joined = await ProviderBucketAsync(restored);

            Assert.Equal((CovenantDisclosureCountKind.LowerBound, 2ul), (joined.CountKind, joined.Count));

            Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM disclosure_subject_state WHERE LastFoldedOrdinal < LastAllocatedOrdinal"));
        }

        Assert.Equal(
            new BackupRestoreErasureApplication(0, 0, 0, 0, 0, 0, 0, BackupRestoreErasureScrubStatus.NotApplicable),
            result.Reconciliation!.ErasureApplication);
    }

    [SkippableFact]
    public async Task An_import_of_selected_sessions_neither_drains_nor_touches_live_evidence()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string m = await harness.InsertSagaAsync(M);

        _ = await harness.EraseSagaAsync(m);

        Guid session = await UnboundSessionAsync(harness);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("import.arcbackup");

        string evidenceBefore = await DestinationEvidenceHexAsync(harness);

        CountingOsCredentialStore keychain = new(harness.Credentials);

        BackupRestoreResult result = await harness.CreateRestoreService(credentials: keychain).RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.ImportSelectedSessions,
                SessionIds: [session],
                Confirmed: true,
                CreateSafetyBackup: false),
            MemoryErasureRestoreHarness.Passphrase.AsMemory(),
            Token);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Null(result.Reconciliation?.ErasureApplication);

        Assert.DoesNotContain(
            result.Phases,
            static p => p.Detail.StartsWith("Drained", StringComparison.Ordinal)
                || p.Detail.StartsWith("Applied destination erasure evidence", StringComparison.Ordinal));

        Assert.Equal(evidenceBefore, await DestinationEvidenceHexAsync(harness));

        // No evidence read, so no Present value, and so no drain.
        Assert.Equal(0, keychain.Calls);
    }

    /// <summary>
    /// Staged reclamation deletes a key row, and every deleter of a key row removes that key's curation
    /// in the same transaction. The schema does not enforce it, so this case proves it: a pin the archive
    /// carried for an erased key is gone with the key, and the key re-created afterwards is not bound by
    /// it.
    /// </summary>
    [SkippableFact]
    public async Task Staged_reclamation_removes_the_keys_curation_so_an_old_pin_never_binds_its_re_creation()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep the vault key offline.");

        await CurateAsync(harness, CovenantCurationKind.Pin, Vault);

        await harness.StopHostAsync();

        await using (SqliteConnection archived = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            Assert.True(await CountAsync(archived, "SELECT COUNT(*) FROM covenant_curation_heads WHERE NormalizedKey = $key AND IsPinned = 1", ("$key", Vault)) > 0);
        }

        string archive = await harness.CreateArchiveAsync("pinned.arcbackup");

        harness.StartHost(covenant: true);

        _ = await harness.EraseCovenantAsync(CovenantScope.Global, null, Vault);

        await harness.StopHostAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 0, covenant: 1, receipts: 1);

        await using (SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret))
        {
            foreach (string table in (string[])["covenant_curation_heads", "covenant_curation_versions", "covenant_curation_receipts", "covenant_key_epochs"])
            {
                Assert.Equal(0L, await CountAsync(restored, $"SELECT COUNT(*) FROM {table} WHERE NormalizedKey = $key", ("$key", Vault)));
            }
        }

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "A new key, made after the restore.");

        MemoryErasurePreflightDto preflight = await PrepareCovenantErasureAsync(harness, Vault);

        Assert.False(preflight.Plan.Covenant!.IsPinned);
    }

    /// <summary>
    /// The staged drain leaves a Covenant tier that is at head but drifted to the evidence step, which
    /// must then prove its purge or refuse; it never commits a purge it could not prove. A full-text index
    /// whose delete trigger was rewritten keeps the erased entry's row after its search document goes, so
    /// the absence proof finds it and the restore refuses; a drift that does not touch what the purge
    /// removes leaves a purge that is proven, and the restore commits without the entry.
    /// </summary>
    [SkippableTheory]
    [InlineData("search-index")]
    [InlineData("canonical-state-trigger")]
    public async Task A_drifted_covenant_tier_with_present_evidence_is_proven_or_refused(string drift)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost(covenant: true);

        _ = await harness.SetCovenantAsync(CovenantScope.Global, null, Vault, "Keep the vault key offline.");

        await SynchronizeSearchAsync(harness);

        await harness.StopHostAsync();

        (string trigger, string drifted) = drift switch
        {
            "search-index" => (
                "covenant_search_documents_ad",
                "CREATE TRIGGER covenant_search_documents_ad AFTER DELETE ON covenant_search_documents BEGIN SELECT 'drifted'; END;"),
            _ => (
                "covenant_state_validate_update",
                "CREATE TRIGGER covenant_state_validate_update BEFORE UPDATE ON covenant_state BEGIN SELECT 'drifted'; END;"),
        };

        // Drifted in the archive only: the live installation is put back before it erases.
        await ExecuteOnLiveAsync(harness, $"DROP TRIGGER {trigger};{drifted}");

        string archive = await harness.CreateArchiveAsync($"drifted-{drift}.arcbackup");

        await ExecuteOnLiveAsync(harness, $"DROP TRIGGER {trigger};{await File.ReadAllTextAsync(TriggerSource(trigger))}");

        harness.StartHost(covenant: true);

        _ = await harness.EraseCovenantAsync(CovenantScope.Global, null, Vault);

        await harness.StopHostAsync();

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive);

        if (drift == "search-index")
        {
            Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

            BackupVerifyIssue issue = Assert.Single(result.Issues);

            Assert.Equal(VerificationFailed, issue.Code);

            Assert.Contains("covenant_fts", issue.Message, StringComparison.Ordinal);

            Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

            AssertNoStagingRemains(harness);

            return;
        }

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        AssertExecuteTimeEvidence(result, saga: 0, lexicon: 0, covenant: 1, receipts: 1);

        Assert.Equal(1, result.Reconciliation!.ErasureApplication!.CovenantEntriesRemoved);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        Assert.Equal(0L, await CountAsync(restored, "SELECT COUNT(*) FROM covenant_entries WHERE NormalizedKey = $key", ("$key", Vault)));

        Assert.Equal(1L, await CountAsync(restored, "SELECT COUNT(*) FROM sqlite_master WHERE name = $trigger AND sql LIKE '%drifted%'", ("$trigger", trigger)));
    }

    /// <summary>
    /// Index blocks that still hold the fact's token. FTS5 stores a term after the one before it as the
    /// length of their shared prefix and the rest of its bytes, and <c>qqzsigiltoken</c> sorts right after
    /// the name's own <c>qqz</c>, so the bytes a block holds for it are <c>sigiltoken</c>.
    /// </summary>
    private const string TokenBlocksSql =
        "SELECT COUNT(*) FROM lexicon_fts_data WHERE instr(block, CAST('sigiltoken' AS BLOB)) > 0";

    private const string SessionTaintSql =
        "SELECT TaintedArtifactCount FROM session_sensitivity_state WHERE lower(replace(SessionId, '-', '')) = $s";

    private const string LabelSql =
        "SELECT COUNT(*) FROM artifact_sensitivity WHERE ArtifactKindCode = 6 AND lower(replace(ArtifactId, '-', '')) = $a";

    /// <summary>Writes M, archives the installation, then erases M on the host.</summary>
    private static async Task<string> ArchiveThenEraseAsync(MemoryErasureRestoreHarness harness)
    {
        harness.StartHost();

        string m = await harness.InsertSagaAsync(M);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("archive-then-erase.arcbackup");

        harness.StartHost();

        _ = await harness.EraseSagaAsync(m);

        await harness.StopHostAsync();

        return archive;
    }

    /// <summary>Writes M and erases it on the host, which creates the key and the evidence.</summary>
    private static async Task EraseOnTheHostAsync(MemoryErasureRestoreHarness harness)
    {
        harness.StartHost();

        _ = await harness.EraseSagaAsync(await harness.InsertSagaAsync(M));

        await harness.StopHostAsync();
    }

    private static void AssertExecuteTimeEvidence(
        BackupRestoreResult result,
        long saga,
        long lexicon,
        long covenant,
        long receipts) =>
        Assert.Equal(
            new BackupRestoreErasureEvidenceSummary(BackupRestoreErasureEvidenceStatus.Present, saga, lexicon, covenant, receipts),
            result.Plan.DestinationErasureEvidence);

    private static Task<BackupRestoreResult> RestoreAsync(
        BackupRestoreService service,
        string archive,
        BackupProtectedStateMode mode = BackupProtectedStateMode.Reject) =>
        service.RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.ReplaceInstallation,
                Confirmed: true,
                CreateSafetyBackup: false,
                ProtectedStateMode: mode,
                ProtectedStateConfirmed: mode is not BackupProtectedStateMode.Reject),
            MemoryErasureRestoreHarness.Passphrase.AsMemory(),
            Token);

    private static async Task<string> DestinationEvidenceHexAsync(MemoryErasureRestoreHarness harness)
    {
        await using SqliteConnection live = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        return await MemoryErasureRestoreHarness.EvidenceHexAsync(live);
    }

    /// <summary>Raw DDL against the stopped installation, standing in for a hand edit or a foreign build.</summary>
    private static async Task ExecuteOnLiveAsync(MemoryErasureRestoreHarness harness, string sql)
    {
        await using SqliteConnection live = await BackupRestoreDatabaseWorker.OpenAsync(
            harness.DatabasePath,
            GrimoireFixture.TestGrimoireSecret,
            readOnly: false,
            Token);

        await ExecuteAsync(live, sql);
    }

    private static string TriggerSource(string trigger) =>
        Directory
            .EnumerateFiles(
                Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Infrastructure", "Data", "Schema"),
                trigger + ".sql",
                SearchOption.AllDirectories)
            .Single(static path => !path.Contains($"{Path.DirectorySeparatorChar}Transitions{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>Runs one search-outbox synchronization the way the maintenance pass runs it.</summary>
    private static async Task SynchronizeSearchAsync(MemoryErasureRestoreHarness harness)
    {
        await using AsyncServiceScope scope = harness.Host.Services.CreateAsyncScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        if (db.Database.GetDbConnection().State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(Token);
        }

        Result<CovenantOutboxSyncOutcome> synchronized = await scope.ServiceProvider
            .GetRequiredService<CovenantSearchOutboxCoordinator>()
            .SynchronizeAsync(CovenantSearchOutboxWorker.DefaultBatchRows, Token);

        Assert.True(synchronized.IsSuccess, synchronized.IsFailure ? synchronized.Error.Message : null);
    }

    /// <summary>Prepares and commits one curation change of a Global key's Confirmed lane through the curation routes.</summary>
    private static async Task CurateAsync(MemoryErasureRestoreHarness harness, CovenantCurationKind kind, string key)
    {
        CovenantCurationPrepareRequest prepare = new(kind, CovenantScope.Global, null, key, CovenantLane.Confirmed, 0, Guid.NewGuid());

        CovenantCurationPreflightDto preflight;

        using (HttpResponseMessage prepared = await harness.PostAsync(
            "/api/memory/covenant/curate/prepare",
            prepare,
            ArcanumJsonContext.Default.CovenantCurationPrepareRequest))
        {
            preflight = await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseCovenantCurationPreflightDto);
        }

        using HttpResponseMessage curated = await harness.PostAsync(
            "/api/memory/covenant/curate",
            new CovenantCurationRequest(
                prepare.Kind,
                prepare.Scope,
                prepare.CampaignId,
                prepare.Key,
                prepare.Lane,
                prepare.ExpectedRevision,
                prepare.MutationId,
                preflight.PreflightToken),
            ArcanumJsonContext.Default.CovenantCurationRequest);

        Assert.Equal(HttpStatusCode.OK, curated.StatusCode);
    }

    /// <summary>The erase preflight of one Global key as the CLI builds it from the detail route, prepared and not applied.</summary>
    private static async Task<MemoryErasurePreflightDto> PrepareCovenantErasureAsync(MemoryErasureRestoreHarness harness, string key)
    {
        CovenantDetailDto detail;

        using (HttpResponseMessage shown = await harness.PostAsync(
            "/api/memory/covenant/detail",
            new CovenantDetailRequest(CovenantScope.Global, null, key),
            ArcanumJsonContext.Default.CovenantDetailRequest))
        {
            Assert.Equal(HttpStatusCode.OK, shown.StatusCode);

            detail = await MemoryErasureRouteDriver.ReadDataAsync(shown, ArcanumJsonContext.Default.ApiResponseCovenantDetailDto);
        }

        using HttpResponseMessage prepared = await harness.PostAsync(
            "/api/memory/covenant/erase/prepare",
            new CovenantErasePrepareRequest(
                CovenantScope.Global,
                null,
                key,
                detail.EntryId!.Value,
                detail.Confirmed is { } confirmed ? new(confirmed.VersionId, confirmed.LaneRevision) : null,
                detail.Proposed is { } proposed ? new(proposed.VersionId, proposed.LaneRevision) : null,
                Guid.NewGuid()),
            ArcanumJsonContext.Default.CovenantErasePrepareRequest);

        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);

        return await MemoryErasureRouteDriver.ReadDataAsync(prepared, ArcanumJsonContext.Default.ApiResponseMemoryErasurePreflightDto);
    }

    /// <summary>Prepares and applies a key reset through its two routes, which creates a new key.</summary>
    private static async Task ResetKeyAsync(MemoryErasureRestoreHarness harness)
    {
        MemoryErasureKeyResetPreflightDto prepared;

        using (HttpResponseMessage response = await harness.Host.CreateAuthenticatedClient()
            .PostAsync("/api/memory/erasure/reset-key/prepare", content: null))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            prepared = await MemoryErasureRouteDriver.ReadDataAsync(
                response,
                ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetPreflightDto);
        }

        using HttpResponseMessage reset = await harness.PostAsync(
            "/api/memory/erasure/reset-key",
            new MemoryErasureKeyResetRequest(prepared.PreflightToken),
            ArcanumJsonContext.Default.MemoryErasureKeyResetRequest);

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        Assert.True((await MemoryErasureRouteDriver.ReadDataAsync(reset, ArcanumJsonContext.Default.ApiResponseMemoryErasureKeyResetResultDto)).KeyCreated);
    }

    /// <summary>Scribes one entry through the host's own Lexicon service.</summary>
    private static async Task ScribeAsync(MemoryErasureRestoreHarness harness, string name, Guid? campaignId, string[] facts)
    {
        using IServiceScope scope = harness.Host.Services.CreateScope();

        Result<LexiconEntryDto> scribed = await scope.ServiceProvider
            .GetRequiredService<ILexiconService>()
            .UpsertAsync(name, "Person", facts, LexiconScope.ForResolvedCampaign(campaignId), Token);

        Assert.True(scribed.IsSuccess, scribed.IsFailure ? scribed.Error.Message : null);
    }

    /// <summary>Labels one Saga memory through the one production writer of sensitivity labels.</summary>
    private static async Task LabelAsync(
        MemoryErasureRestoreHarness harness,
        string memoryId,
        Guid? sessionId,
        Guid? campaignId,
        string content)
    {
        using IServiceScope scope = harness.Host.Services.CreateScope();

        Result<LabeledArtifactWriteReceipt> labelled = await scope.ServiceProvider
            .GetRequiredService<IArtifactSensitivityLedger>()
            .LabelAsync(
                new DerivedArtifactWrite(
                    SensitiveArtifactKind.Saga,
                    Guid.Parse(memoryId),
                    sessionId,
                    campaignId,
                    turnId: null,
                    artifactRevision: 1,
                    DerivedArtifactContentDigest.ForText(content),
                    ContentSensitivity.CovenantDerived,
                    GenerationProvenance.CreateExact([Guid.NewGuid()])),
                Token);

        Assert.True(labelled.IsSuccess, labelled.IsFailure ? labelled.Error.Message : null);
    }

    /// <summary>Registers one Campaign through its route.</summary>
    private static async Task<Guid> RegisterCampaignAsync(MemoryErasureRestoreHarness harness, string suffix)
    {
        string path = Path.Combine(harness.Profile.TempHome, $"restore-campaign-{suffix}");

        Directory.CreateDirectory(path);

        using HttpResponseMessage registered = await harness.PostAsync(
            "/api/campaigns",
            new RegisterCampaignRequest($"Restore {suffix}", path, WorkspaceType.Campaign, null),
            ArcanumJsonContext.Default.RegisterCampaignRequest);

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(registered, ArcanumJsonContext.Default.ApiResponseCampaignDto)).Id;
    }

    /// <summary>
    /// Registers one Campaign through its route, then creates one Session bound to it through the
    /// turn-begin store, the writer that records a Session's Campaign binding.
    /// </summary>
    private static async Task<(Guid Campaign, Guid Session)> BoundSessionAsync(MemoryErasureRestoreHarness harness, string suffix)
    {
        Guid campaign = await RegisterCampaignAsync(harness, suffix);

        using IServiceScope scope = harness.Host.Services.CreateScope();

        Result<Guid> session = await scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>().CreateBoundSessionAsync(
            CanonicalCampaignContext.Create(
                SessionCampaignBinding.ForCampaign(campaign),
                campaignAvailabilityGeneration: 1,
                pathIdentityPolicyVersion: 1,
                pathIdentityRevision: null,
                rootIdentityDigest: null),
            $"Restore session {suffix}",
            Token);

        Assert.True(session.IsSuccess, session.IsFailure ? session.Error.Message : null);

        return (campaign, session.Value);
    }

    /// <summary>A Session created through its route.</summary>
    private static async Task<Guid> UnboundSessionAsync(MemoryErasureRestoreHarness harness)
    {
        using HttpResponseMessage created = await harness.PostAsync(
            "/api/sessions",
            new CreateSessionRequest(null, "Imported session"),
            ArcanumJsonContext.Default.CreateSessionRequest);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        return (await MemoryErasureRouteDriver.ReadDataAsync(created, ArcanumJsonContext.Default.ApiResponseSessionDetailDto)).Id;
    }

    /// <summary>The one nonrevocable provider bucket, read through the store every bucket writer uses.</summary>
    private static async Task<CovenantDisclosureState> ProviderBucketAsync(SqliteConnection connection) =>
        Assert.Single(
            await ExternalDisclosureStateStore.ReadAllAsync(connection, null, Token),
            static bucket => bucket.Destination is CovenantEgressDestination.Provider
                && bucket.Revocability is CovenantDisclosureRevocability.Nonrevocable);

    /// <summary>
    /// The production keyring for every probe, but a latch copy of some other key: what a provider whose
    /// latch moved after the destination read would hand the evidence step.
    /// </summary>
    private sealed class ForeignLatchedKey(IMemoryErasureKeyProvider inner) : IMemoryErasureKeyProvider
    {
        public MemoryErasureKeyLatch Latch => inner.Latch;

        public MemoryErasureKeyOpenResult OpenExisting(MemoryErasureKeyProbe probe) => inner.OpenExisting(probe);

        public MemoryErasureKey? TryCopyLatched() =>
            MemoryErasureKey.FromBytes(System.Security.Cryptography.RandomNumberGenerator.GetBytes(MemoryErasureDigestGrammar.KeyBytes));
    }

    private static CovenantDisclosureDraft Draft(Guid subject, byte effectSeed, long timestamp) =>
        new(
            Guid.Parse("21212121-3434-4545-8656-787878787878"),
            CovenantDisclosureSubjectKind.Turn,
            subject,
            CovenantTask6Fixture.D(effectSeed),
            CovenantEgressDestination.Provider,
            CovenantDisclosureRevocability.Nonrevocable,
            CovenantTask6Fixture.D(80),
            PreFoldDisclosureHistory.Sensitivity.Digest,
            null,
            CovenantTask6Fixture.D(82),
            null,
            timestamp);

    private static string Hash(string content) => Convert.ToHexString(AnnalContentDigest.ForSagaMemory(content));

    private static string Key(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static string Key(Guid id) => id.ToString("N");

    private static void AssertNoStagingRemains(MemoryErasureRestoreHarness harness) =>
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(harness.InstallationRoot)!, BackupRestoreJournal.StagingPrefix + "*"));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return Assert.IsType<string>(await command.ExecuteScalarAsync(Token));
    }
}
