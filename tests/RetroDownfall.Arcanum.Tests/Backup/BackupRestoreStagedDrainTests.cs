using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

/// <summary>
/// A restore whose destination holds erasure evidence drains an older archive's staged generation through
/// every sweep-bearing step of every tier before anything else happens to it, and refuses before any
/// displacement when that cannot be done.
/// </summary>
/// <remarks>
/// <para>An archive older than this build's schema is migrated forward in staging. Without evidence the
/// migration stops at the first step that carries a sweep, exactly as it always has, and the host finishes
/// the sweep after commit. With evidence that is not enough: the evidence can only be joined against a
/// staged generation at every head, so a staged copy with a pending run or a tier recorded below head is
/// drained there first. A drain that throws, stalls or meets a refused tier is the typed refusal rather
/// than a restore that commits evidence it could not apply; the operator cancelling it is a cancellation,
/// exactly as it is anywhere else in a restore.</para>
///
/// <para>Every case erases through the host's routes on a real profile, builds the archive as a separate
/// installation through the production backup service, and restores it through the production restore
/// service over the same in-memory keychain.</para>
/// </remarks>
[Collection("ApiHost")]
public sealed class BackupRestoreStagedDrainTests
{
    private const string Erased = "erased before the restore";

    private const string Kept = "written and never erased";

    private const string Unjoinable = "backup.restore_erasure_evidence_unjoinable";

    private const string ArchivedMemoryId = "6F1D2C3B-4A59-4E68-8D7C-1B2A3F4E5D6C";

    private static CancellationToken Token => CancellationToken.None;

    [SkippableFact]
    public async Task An_archive_at_core_eleven_and_canonical_five_is_drained_to_every_head_before_commit()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.Equal(6L, await ScalarAsync(restored, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 1"));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions"));

        Assert.True(await BackupRestoreDatabaseWorker.TableExistsAsync(restored, "memory_erasure_fingerprints", Token));

        Assert.Contains(result.Phases, static p => p.Phase == BackupRestorePhase.Migrate && p.Detail.StartsWith("Drained staged schema transitions", StringComparison.Ordinal));

        // The drain carried the archive's own rows to head; it did not lose them on the way.
        Assert.Equal(1L, await ScalarAsync(restored, $"SELECT COUNT(*) FROM saga_memories WHERE Id = '{ArchivedMemoryId}'"));
    }

    /// <summary>
    /// The drain follows every tier, not only Core: an archive whose canonical tier also stops at a sweep
    /// can only reach that sweep once Core is at head, so it takes a second pass.
    /// </summary>
    [SkippableFact]
    public async Task An_archive_with_sweeps_pending_in_two_tiers_is_drained_through_both()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven-canonical-four",
            new(
            [
                CoreSchemaVersionElevenFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.Core),
                CovenantCanonicalSchemaVersionFourFixture.ChainSet().ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]),
            SeedArchivedMemoryAsync);

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.Equal(6L, await ScalarAsync(restored, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 1"));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT SchemaVersion FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 2"));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions"));

        Assert.Contains(result.Phases, static p => p.Phase == BackupRestorePhase.Migrate && p.Detail.StartsWith("Drained staged schema transitions: 2 passes,", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task An_archive_already_at_head_needs_no_drain()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string memory = await harness.InsertSagaAsync(Erased);

        _ = await harness.InsertSagaAsync(Kept);

        await harness.StopHostAsync();

        string archive = await harness.CreateArchiveAsync("at-head.arcbackup");

        harness.StartHost();

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();

        CountingBackupService safetyBackups = new();

        BackupRestoreResult result = await RestoreAsync(
            harness.CreateRestoreService(safetyBackups: () => safetyBackups),
            archive,
            createSafetyBackup: true);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(BackupRestoreErasureEvidenceStatus.Present, result.Plan.DestinationErasureEvidence?.Status);

        // The positive control for every "no safety backup was taken" assertion here: a restore that
        // proceeds asks this fake for exactly one.
        Assert.Equal(1, safetyBackups.CreateCalls);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(GrimoireFixture.TestGrimoireSecret);

        Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.DoesNotContain(result.Phases, static p => p.Detail.StartsWith("Drained", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_destination_without_evidence_leaves_an_older_archive_pending_exactly_as_before()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await WriteWithoutErasingAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(BackupRestoreErasureEvidenceStatus.None, result.Plan.DestinationErasureEvidence?.Status);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(11, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions"));

        Assert.DoesNotContain(result.Phases, static p => p.Detail.StartsWith("Drained", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_failing_staged_sweep_refuses_as_unjoinable_before_any_displacement()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        string before = await harness.LiveDatabaseDigestAsync();

        CountingBackupService safetyBackups = new();

        BackupRestoreService service = harness.CreateRestoreService(
            installer: InstallerWithCoreSweep(static name => new ThrowingBackfill(name)),
            safetyBackups: () => safetyBackups);

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath, createSafetyBackup: true);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        Assert.EndsWith("Diagnostics: InvalidOperationException", issue.Message, StringComparison.Ordinal);

        Assert.DoesNotContain("sentinel-diagnostic", issue.Message, StringComparison.Ordinal);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        Assert.Equal(0, safetyBackups.CreateCalls);

        AssertNoStagingRemains(harness);
    }

    [SkippableFact]
    public async Task A_failing_staged_sweep_without_destination_evidence_still_restores()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await WriteWithoutErasingAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        BackupRestoreService service = harness.CreateRestoreService(
            installer: InstallerWithCoreSweep(static name => new ThrowingBackfill(name)));

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(11, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions"));
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task A_drain_pass_that_makes_no_progress_is_refused_rather_than_looping()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        string before = await harness.LiveDatabaseDigestAsync();

        StalledBackfill? stalled = null;

        BackupRestoreService service = harness.CreateRestoreService(
            installer: InstallerWithCoreSweep(name => stalled = new StalledBackfill(name)));

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        // A stall is named by the tier health it left behind, not by an exception.
        Assert.EndsWith("Diagnostics: TransitionIncomplete", issue.Message, StringComparison.Ordinal);

        Assert.True(stalled!.Batches > 0, "The pending sweep was attempted before the pass was judged stalled.");

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
    }

    /// <summary>
    /// A journal row written for an older head than this build's cannot be finished by it. Without
    /// evidence that is the generic restore failure it has always been; with evidence it is the typed
    /// refusal, because the staged generation cannot be brought to the head the evidence needs.
    /// </summary>
    [SkippableFact]
    public async Task An_archive_journaled_mid_transition_for_an_older_head_refuses_with_the_typed_code()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        harness.StartHost();

        string memory = await harness.InsertSagaAsync(Erased);

        await harness.StopHostAsync();

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "older-head",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            static async connection =>
            {
                await SeedArchivedMemoryAsync(connection);

                // A build whose head was version 12 started the run toward it and stopped at its sweep.
                GrimoireSchemaInstallResult older = await GrimoireSchemaTestInstaller.InstallAsync(
                    connection,
                    CoreSchemaVersionTwelveFixture.ChainSet(),
                    1536,
                    Token);

                Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, older.Core.Health);
            });

        string withoutEvidence = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult unchanged = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Rejected, unchanged.Status);

        Assert.Equal("backup.restore_failed", Assert.Single(unchanged.Issues).Code);

        Assert.Equal(withoutEvidence, await harness.LiveDatabaseDigestAsync());

        harness.StartHost();

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();

        string withEvidence = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult refused = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Rejected, refused.Status);

        BackupVerifyIssue issue = Assert.Single(refused.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        Assert.EndsWith("Diagnostics: GrimoireSchemaRefusedException", issue.Message, StringComparison.Ordinal);

        Assert.Equal(withEvidence, await harness.LiveDatabaseDigestAsync());
    }

    /// <summary>
    /// The operator's Ctrl+C during the drain is a cancellation, not a verdict on the archive: it leaves
    /// the restore exactly as a cancellation during the migration does, which the CLI reports as 130,
    /// with nothing displaced and nothing staged left behind.
    /// </summary>
    [SkippableFact]
    public async Task A_drain_cancelled_by_the_caller_exits_as_cancelled_with_nothing_displaced()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        string before = await harness.LiveDatabaseDigestAsync();

        using CancellationTokenSource cancellation = new();

        CountingBackupService safetyBackups = new();

        BackupRestoreService service = harness.CreateRestoreService(
            installer: InstallerWithCoreSweep(name => new ThrowingBackfill(
                name,
                () =>
                {
                    cancellation.Cancel();

                    return new OperationCanceledException(cancellation.Token);
                })),
            safetyBackups: () => safetyBackups);

        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RestoreAsync(service, archive.ArchivePath, createSafetyBackup: true, cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested, "The sweep ran and cancelled the restore.");

        // What the CLI does with it: the cancellation exit code and message, never the unjoinable advice.
        CliFailure failure = CliFailureMapper.Map(cancelled);

        Assert.Equal(CliExitCode.Cancelled, failure.ExitCode);

        Assert.Equal(130, (int)failure.ExitCode);

        Assert.Equal("The operation was cancelled.", failure.SafeMessage);

        Assert.Equal(0, safetyBackups.CreateCalls);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        AssertNoStagingRemains(harness);
    }

    /// <summary>
    /// A cancellation nobody asked for, raised from inside a sweep, says nothing about the operator: it is
    /// a drain that could not finish, so it is the typed refusal like any other throw.
    /// </summary>
    [SkippableFact]
    public async Task A_cancellation_the_caller_did_not_request_is_still_unjoinable()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreService service = harness.CreateRestoreService(
            installer: InstallerWithCoreSweep(static name => new ThrowingBackfill(
                name,
                static () => new OperationCanceledException())));

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        Assert.EndsWith("Diagnostics: OperationCanceledException", issue.Message, StringComparison.Ordinal);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
    }

    /// <summary>
    /// A Covenant tier that refuses while the drain is moving it does not refuse by throwing, as Core
    /// does: it reports a health. An archive journaled toward an older canonical head is the case: with
    /// evidence that health is the typed refusal, and without it the restore goes on exactly as before.
    /// </summary>
    /// <param name="evidence">Whether the destination erased something before the restore.</param>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_covenant_tier_journaled_toward_an_older_head_refuses_only_when_evidence_must_be_joined(bool evidence)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await (evidence ? EraseOnTheHostAsync(harness) : WriteWithoutErasingAsync(harness));

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "canonical-older-head",
            CovenantCanonicalSchemaVersionFourFixture.ChainSet(),
            static async connection =>
            {
                // A build whose canonical head was version 5 started the run toward it and stopped at
                // that step's sweep.
                GrimoireSchemaInstallResult older = await GrimoireSchemaTestInstaller.InstallAsync(
                    connection,
                    CovenantCanonicalSchemaVersionFiveFixture.ChainSet(),
                    1536,
                    Token);

                Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, older.CovenantCanonical.Health);
            });

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        if (!evidence)
        {
            Assert.Equal(BackupRestoreStatus.Completed, result.Status);

            Assert.DoesNotContain(result.Phases, static p => p.Detail.StartsWith("Drained", StringComparison.Ordinal));

            return;
        }

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        Assert.EndsWith("Diagnostics: TransitionUnresumable", issue.Message, StringComparison.Ordinal);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
    }

    /// <summary>
    /// The accelerator is a tier the drain answers for too. One whose metadata row is gone is recorded
    /// at no version at all, so it is not at head, and the health that keeps it there refuses.
    /// </summary>
    /// <param name="evidence">Whether the destination erased something before the restore.</param>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unrecorded_accelerator_tier_refuses_only_when_evidence_must_be_joined(bool evidence)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await (evidence ? EraseOnTheHostAsync(harness) : WriteWithoutErasingAsync(harness));

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "accelerator-unrecorded",
            GrimoireSchemaVersionChains.Default,
            static connection => ExecuteAsync(
                connection,
                "DELETE FROM grimoire_feature_schemas WHERE FamilyCode = 1 AND TransactionTierCode = 2;"));

        string before = await harness.LiveDatabaseDigestAsync();

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        if (!evidence)
        {
            Assert.Equal(BackupRestoreStatus.Completed, result.Status);

            return;
        }

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        Assert.EndsWith("Diagnostics: MetadataMissing", issue.Message, StringComparison.Ordinal);

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());
    }

    /// <summary>
    /// A tier recorded at head needs no drain, whatever its health: no pass can repair a drifted catalog,
    /// so the drain leaves it to the evidence step's own verification rather than refusing an archive it
    /// has nothing to drain.
    /// </summary>
    [SkippableFact]
    public async Task A_drifted_covenant_tier_at_head_is_not_a_drain_failure()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "accelerator-drifted",
            GrimoireSchemaVersionChains.Default,
            static connection => ExecuteAsync(
                connection,
                """
                DROP TRIGGER covenant_search_documents_ai;

                CREATE TRIGGER covenant_search_documents_ai
                AFTER INSERT ON covenant_search_documents
                BEGIN
                    SELECT 'drifted';
                END;
                """));

        BackupRestoreResult result = await RestoreAsync(harness.CreateRestoreService(), archive.ArchivePath);

        Assert.Empty(result.Issues);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(BackupRestoreErasureEvidenceStatus.Present, result.Plan.DestinationErasureEvidence?.Status);

        Assert.Contains(result.Phases, static p => p.Phase == BackupRestorePhase.Reconcile);

        Assert.DoesNotContain(result.Phases, static p => p.Detail.StartsWith("Drained", StringComparison.Ordinal));

        // The archive really was drifted when it was staged, and nothing healed it on the way.
        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(1L, await ScalarAsync(restored, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'covenant_search_documents_ai' AND sql LIKE '%drifted%'"));
    }

    /// <summary>
    /// A sweep longer than one pass is drained across passes. Progress inside one step is the journal's
    /// cursor and row count, not a tier version, so the stall guard has to read it.
    /// </summary>
    [SkippableFact]
    public async Task A_sweep_longer_than_one_pass_is_drained_across_passes()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        const int total = (2 * BackupRestoreSchemaDrain.MaxBatchesPerPass) + 1;

        CountingBackfill? counting = null;

        BackupRestoreService service = harness.CreateRestoreService(
            installer: InstallerWithCoreSweep(name => counting = new CountingBackfill(name, total)));

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        Assert.Equal(total, counting!.Batches);

        Assert.Contains(
            result.Phases,
            static p => p.Phase == BackupRestorePhase.Migrate
                && p.Detail == $"Drained staged schema transitions: 3 passes, {total} batches, {total} rows.");

        await using SqliteConnection restored = await harness.OpenLiveDatabaseAsync(archive.GrimoireSecret);

        Assert.Equal(13, await GrimoireCoreSchemaVersion.ReadAsync(restored, Token));

        Assert.Equal(0L, await ScalarAsync(restored, "SELECT COUNT(*) FROM grimoire_schema_transitions"));
    }

    /// <summary>
    /// With the gate on, the Covenant restore session is already open when the drain refuses: admission
    /// is closed under the restore's owner. The refusal aborts that session as proven pre-swap, before
    /// the staged Covenant reconcile, and the installation is exactly as it was.
    /// </summary>
    [SkippableFact]
    public async Task A_drain_refusal_with_the_covenant_gate_on_reopens_admission_with_nothing_displaced()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        await EraseOnTheHostAsync(harness);

        MemoryErasureRestoreHarness.ArchivedInstallation archive = await harness.CreateArchiveAtAsync(
            "core-eleven",
            MemoryErasureRestoreHarness.CoreElevenCanonicalFive(),
            SeedArchivedMemoryAsync);

        string before = await harness.LiveDatabaseDigestAsync();

        CovenantRestoreStagingTests.RecordingExclusiveGate gate = new();

        BackupRestoreService service = harness.CreateRestoreService(
            options: new BackupRestoreServiceOptions { RestoreStaging = harness.CovenantStaging(gate) },
            installer: InstallerWithCoreSweep(static name => new ThrowingBackfill(name)));

        BackupRestoreResult result = await RestoreAsync(service, archive.ArchivePath);

        Assert.Equal(BackupRestoreStatus.Rejected, result.Status);

        BackupVerifyIssue issue = Assert.Single(result.Issues);

        Assert.Equal(Unjoinable, issue.Code);

        Assert.EndsWith("Diagnostics: InvalidOperationException", issue.Message, StringComparison.Ordinal);

        // The session was open, and nothing durable happened, so admission reopens.
        Assert.Equal(1, gate.ExclusiveAcquisitions);

        Assert.Equal([CovenantExclusiveLeaseDisposition.RollbackAndReopen], gate.Dispositions);

        Assert.DoesNotContain(result.Phases, static p => p.Detail.StartsWith("Stripped the archive's managed-file authority", StringComparison.Ordinal));

        Assert.Equal(before, await harness.LiveDatabaseDigestAsync());

        AssertNoStagingRemains(harness);
    }

    /// <summary>Writes one Global memory, then erases it, which creates the key and the evidence.</summary>
    private static async Task EraseOnTheHostAsync(MemoryErasureRestoreHarness harness)
    {
        harness.StartHost();

        string memory = await harness.InsertSagaAsync(Erased);

        _ = await harness.EraseSagaAsync(memory);

        await harness.StopHostAsync();
    }

    /// <summary>Writes one Global memory and erases nothing, so the destination holds no evidence.</summary>
    private static async Task WriteWithoutErasingAsync(MemoryErasureRestoreHarness harness)
    {
        harness.StartHost();

        _ = await harness.InsertSagaAsync(Kept);

        await harness.StopHostAsync();
    }

    /// <summary>One Global Saga memory, through the columns every catalog from version 4 on declares.</summary>
    private static async Task SeedArchivedMemoryAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO saga_memories (Id, Content, CreatedAt, ScopeKindCode)
            VALUES ($id, 'archived at an older schema', '2026-01-01T00:00:00.0000000Z', 1);
            """;

        _ = command.Parameters.AddWithValue("$id", ArchivedMemoryId);

        Assert.Equal(1, await command.ExecuteNonQueryAsync(Token));
    }

    private static Task<BackupRestoreResult> RestoreAsync(
        BackupRestoreService service,
        string archive,
        bool createSafetyBackup = false,
        CancellationToken cancellationToken = default) =>
        service.RestoreAsync(
            new BackupRestoreRequest(
                archive,
                BackupRestoreConflictMode.ReplaceInstallation,
                Confirmed: true,
                CreateSafetyBackup: createSafetyBackup),
            MemoryErasureRestoreHarness.Passphrase.AsMemory(),
            cancellationToken);

    /// <summary>
    /// This build's installer, with Core's sweep-bearing step to version 12 carrying
    /// <paramref name="replace"/>'s sweep under the name the journal records.
    /// </summary>
    private static GrimoireSchemaInstaller InstallerWithCoreSweep(Func<string, IGrimoireSchemaBackfill> replace)
    {
        GrimoireSchemaVersionChain core = GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core);

        GrimoireSchemaVersionChain replaced = new(
            core.HeadManifest,
            core.HeadObjects,
            [
                .. core.Steps.Select(step => step.ToVersion == 12
                    ? step with { Backfill = replace(step.Backfill!.Name) }
                    : step),
            ]);

        return GrimoireSchemaTestInstaller.Create(
            new(
            [
                replaced,
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantCanonical),
                GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.CovenantAccelerator),
            ]));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToInt64(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertNoStagingRemains(MemoryErasureRestoreHarness harness) =>
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(harness.InstallationRoot)!, ".arcanum-restore-*"));

    /// <summary>A sweep that fails on its first batch, with a diagnostic no refusal may repeat.</summary>
    private sealed class ThrowingBackfill(string name, Func<Exception>? fault = null) : IGrimoireSchemaBackfill
    {
        public string Name => name;

        public int MaxRowsPerBatch => 100;

        public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string? cursor,
            CancellationToken cancellationToken) =>
            Task.FromException<GrimoireSchemaBackfillBatch>(
                fault?.Invoke() ?? new InvalidOperationException("sentinel-diagnostic"));
    }

    /// <summary>A sweep that never finishes and never moves: every batch hands its cursor straight back.</summary>
    private sealed class StalledBackfill(string name) : IGrimoireSchemaBackfill
    {
        private int _batches;

        public string Name => name;

        public int MaxRowsPerBatch => 100;

        public int Batches => Volatile.Read(ref _batches);

        public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string? cursor,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _batches);

            return Task.FromResult(new GrimoireSchemaBackfillBatch(cursor, 0, IsComplete: false));
        }
    }

    /// <summary>
    /// A sweep that drains one row per batch from an integer cursor and completes at
    /// <paramref name="total"/>, so its length in batches is exact.
    /// </summary>
    private sealed class CountingBackfill(string name, int total) : IGrimoireSchemaBackfill
    {
        private int _batches;

        public string Name => name;

        public int MaxRowsPerBatch => 1;

        public int Batches => Volatile.Read(ref _batches);

        public Task<GrimoireSchemaBackfillBatch> AdvanceBatchAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string? cursor,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _batches);

            int next = (cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture)) + 1;

            return Task.FromResult(
                new GrimoireSchemaBackfillBatch(
                    next.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    1,
                    IsComplete: next >= total));
        }
    }

    /// <summary>A safety-backup service that only counts how often a restore asked it for one.</summary>
    private sealed class CountingBackupService : IBackupService
    {
        private int _createCalls;

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public Task<BackupPlan> PlanAsync(BackupPlanRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackupCreateResult> CreateAsync(
            BackupCreateRequest request,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _createCalls);

            return Task.FromResult(
                new BackupCreateResult(
                    BackupCreateStatus.Complete,
                    request.OutputPath,
                    ArchiveBytes: 0,
                    Guid.NewGuid(),
                    Manifest: null,
                    new BackupPlan(DateTimeOffset.UnixEpoch, BackupScope.Full, SessionId: null, [], 0, 0, [], []),
                    []));
        }

        public Task<BackupInspectResult> InspectAsync(
            string archivePath,
            ReadOnlyMemory<char>? recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BackupVerifyResult> VerifyAsync(
            string archivePath,
            ReadOnlyMemory<char> recoveryPassphrase,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BackupListItem>> ListAsync(
            string? directory,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
