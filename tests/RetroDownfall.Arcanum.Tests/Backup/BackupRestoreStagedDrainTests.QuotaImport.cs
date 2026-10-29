using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed partial class BackupRestoreStagedDrainTests
{
    [SkippableFact]
    public async Task Selected_import_between_executions_restarts_quota_census_without_losing_the_imported_owner()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using MemoryErasureRestoreHarness harness = await MemoryErasureRestoreHarness.CreateAsync();

        var destination = await harness.CreateArchiveAtAsync("quota-import-destination", CoreSchemaVersionThirtyTwoFixture.ChainSet(), async connection =>
        {
            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = """
                WITH RECURSIVE owners(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM owners WHERE i<129)
                INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt)
                SELECT printf('B0000000-0000-0000-0000-%012X',i),'archived','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z' FROM owners;
                """;

            await seed.ExecuteNonQueryAsync();
        });

        Guid imported = Guid.Parse("A0000000-0000-0000-0000-000000000001");

        var archive = await harness.CreateArchiveAtAsync("quota-import-source", CoreSchemaVersionThirtyTwoFixture.ChainSet(), async connection =>
        {
            await using SqliteCommand seed = connection.CreateCommand();

            seed.CommandText = "INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt) VALUES ('A0000000-0000-0000-0000-000000000001','archived','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

            await seed.ExecuteNonQueryAsync();
        });

        // Place the genuine Core32 fixture's complete database binding in the private installation.
        Directory.CreateDirectory(harness.InstallationRoot);

        string sourceRoot = Path.Combine(harness.Profile.TempHome, "sources", "quota-import-destination");

        File.Copy(Path.Combine(sourceRoot, "arcanum.db"), harness.DatabasePath, true);

        File.Copy(Path.Combine(sourceRoot, "arcanum.db.kdf"), harness.DatabasePath + ".kdf", true);

        using (ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(harness.InstallationRoot).BorrowAcquiredLock())
        await using (SqliteConnection connection = await BackupRestoreDatabaseWorker.OpenAsync(harness.DatabasePath, destination.GrimoireSecret, false, Token))
        {
            await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, Token);

            using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, harness.InstallationRoot, new GrimoireDbReadiness());

            GrimoireSchemaTransitionJournalRow journal = (await GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, Token))!;

            var page = await new GrimoireSchemaBackfillRunner(GrimoireSchemaTestInstaller.Create(), TimeProvider.System).AdvanceAsync(
                connection, GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core), journal,
                GrimoireSchemaTestInstaller.CreateContext(), 1, Token, execution);

            Assert.Equal(128, page.RowsProcessed);
        }

        BackupRestoreResult result = await harness.CreateRestoreService(
            secrets: new MemoryErasureRestoreHarness.FixedGrimoireSecretStore(destination.GrimoireSecret),
            installer: GrimoireSchemaTestInstaller.Create(CoreSchemaVersionThirtyTwoFixture.ChainSet())).RestoreAsync(
                new BackupRestoreRequest(archive.ArchivePath, BackupRestoreConflictMode.ImportSelectedSessions,
                    SessionIds: [imported], Confirmed: true, CreateSafetyBackup: false),
                MemoryErasureRestoreHarness.Passphrase.AsMemory(), Token);

        Assert.Equal(BackupRestoreStatus.Completed, result.Status);

        using ArcanumMaintenanceLock resumedLock = ArcanumMaintenanceLock.AcquireDetailed(harness.InstallationRoot).BorrowAcquiredLock();

        await using SqliteConnection resumed = await BackupRestoreDatabaseWorker.OpenAsync(harness.DatabasePath, destination.GrimoireSecret, false, Token);

        Assert.Equal(130L, await ScalarAsync(resumed, "SELECT COUNT(*) FROM Sessions"));

        using GrimoireSchemaQuiescentExecution resumedExecution = GrimoireSchemaQuiescentExecution.ForStartup(resumed, resumedLock, harness.InstallationRoot, new GrimoireDbReadiness());

        GrimoireSchemaTransitionJournalRow pending = (await GrimoireSchemaTransitionJournal.ReadAsync(resumed, null, GrimoireSchemaTransactionTier.Core, Token))!;

        GrimoireSchemaBackfillProgress completed = await new GrimoireSchemaBackfillRunner(GrimoireSchemaTestInstaller.Create(), TimeProvider.System).AdvanceAsync(
            resumed, GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core), pending,
            GrimoireSchemaTestInstaller.CreateContext(), 16, Token, resumedExecution);

        Assert.True(completed.StepComplete);

        Assert.Equal(260, completed.RowsProcessed);

        Assert.Equal(33, await GrimoireCoreSchemaVersion.ReadAsync(resumed, Token));

        Assert.Null(await GrimoireSchemaTransitionJournal.ReadAsync(resumed, null, GrimoireSchemaTransactionTier.Core, Token));

        Assert.Equal(130L, await ScalarAsync(resumed, "SELECT COUNT(*) FROM session_turn_quota_state"));

        Assert.Equal(0L, await ScalarAsync(resumed, "SELECT ClaimCount FROM installation_turn_quota_state"));
    }
}
