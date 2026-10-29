using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Infrastructure.Backup;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

public sealed class InstallationTurnQuotaEvolutionTests
{
    static InstallationTurnQuotaEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task Core32_census_restarts_once_after_reopen_and_publishes_correction_with_version_and_journal_retirement()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        GrimoireSchemaTransitionJournalRow saved;

        using (ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock())
        await using (SqliteConnection first = await scratch.OpenAsync(CancellationToken.None))
        {
            await SeedCore32Async(first, 129);

            GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(first, 1536, CancellationToken.None);

            Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, installed.Core.Health);

            using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(first, held, root, new GrimoireDbReadiness());

            GrimoireSchemaBackfillProgress page = await AdvanceAsync(first, execution);

            Assert.Equal(128, page.RowsProcessed);

            Assert.False(page.StepComplete);

            saved = (await JournalAsync(first))!;

            Assert.Equal(128, saved.BackfillRowsProcessed);

            Assert.Equal(32, await GrimoireCoreSchemaVersion.ReadAsync(first, CancellationToken.None));

            Assert.Equal(2L, await ScalarAsync(first, "SELECT ClaimCount FROM installation_turn_quota_state"));
        }

        using (ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock())
        await using (SqliteConnection resumed = await scratch.OpenAsync(CancellationToken.None))
        {
            Assert.Equal(saved, await JournalAsync(resumed));

            GrimoireSchemaRefusedException old = await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(() =>
                GrimoireSchemaTestInstaller.InstallAsync(resumed, CoreSchemaVersionThirtyTwoFixture.ChainSet(), 1536, CancellationToken.None));

            Assert.Contains("TransitionUnresumable", old.Message, StringComparison.Ordinal);

            Assert.Equal(saved, await JournalAsync(resumed));

            using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(resumed, held, root, new GrimoireDbReadiness());

            Assert.Equal(128, (await AdvanceAsync(resumed, execution)).RowsProcessed);

            Assert.Equal(128, (await JournalAsync(resumed))!.BackfillRowsProcessed);

            Assert.Equal(1, (await AdvanceAsync(resumed, execution)).RowsProcessed);

            Assert.Equal(128, (await AdvanceAsync(resumed, execution)).RowsProcessed);

            Assert.Equal(32, await GrimoireCoreSchemaVersion.ReadAsync(resumed, CancellationToken.None));

            Assert.Equal(2L, await ScalarAsync(resumed, "SELECT ClaimCount FROM installation_turn_quota_state"));

            GrimoireSchemaBackfillProgress final = await AdvanceAsync(resumed, execution);

            Assert.True(final.StepComplete);

            Assert.Equal(1, final.RowsProcessed);

            Assert.Equal(33, await GrimoireCoreSchemaVersion.ReadAsync(resumed, CancellationToken.None));

            Assert.Null(await JournalAsync(resumed));

            Assert.Equal(1L, await ScalarAsync(resumed, "SELECT ClaimCount FROM installation_turn_quota_state"));

            Assert.Equal(129L, await ScalarAsync(resumed, "SELECT COUNT(*) FROM session_turn_quota_state"));
        }

        await using SqliteConnection reopened = await scratch.OpenAsync(CancellationToken.None);

        Assert.Equal(33, await GrimoireCoreSchemaVersion.ReadAsync(reopened, CancellationToken.None));

        Assert.Null(await JournalAsync(reopened));

        Assert.Equal(1L, await ScalarAsync(reopened, "SELECT ClaimCount FROM installation_turn_quota_state"));

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(reopened, 1536, CancellationToken.None)).Core.IsHealthy);

        GrimoireSchemaRefusedException newer = await Assert.ThrowsAsync<GrimoireSchemaRefusedException>(() =>
            GrimoireSchemaTestInstaller.InstallAsync(reopened, CoreSchemaVersionThirtyTwoFixture.ChainSet(), 1536, CancellationToken.None));

        Assert.Contains("IncompatibleNewerVersion", newer.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("retirement-failure")]
    [InlineData("journal-cas-loss")]
    public async Task Failed_or_cancelled_final_publication_rolls_back_correction_and_retains_the_exact_cursor(string failure)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await SeedCore32Async(connection, 1);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

        await AdvanceAsync(connection, execution);

        GrimoireSchemaTransitionJournalRow before = (await JournalAsync(connection))!;

        using CancellationTokenSource cancellation = new();

        connection.CreateFunction("cancel_census", () => { cancellation.Cancel(); return 0; });

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = failure switch
        {
            "cancel" => "CREATE TEMP TRIGGER final_probe AFTER UPDATE ON installation_turn_quota_state BEGIN SELECT cancel_census(); END;",
            "journal-cas-loss" => "CREATE TEMP TRIGGER final_probe AFTER UPDATE ON installation_turn_quota_state BEGIN UPDATE grimoire_schema_transitions SET Revision=Revision+1; END;",
            _ => "CREATE TEMP TRIGGER final_probe BEFORE DELETE ON grimoire_schema_transitions BEGIN SELECT RAISE(ABORT,'finalization refused'); END;",
        };

        await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => AdvanceAsync(connection, execution, cancellation.Token));

        Assert.Equal(before, await JournalAsync(connection));

        Assert.Equal(32, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT ClaimCount FROM installation_turn_quota_state"));

        Assert.Equal(0L, await ScalarAsync(connection, "SELECT arcanum_turn_capacity_mutation_authorized()"));

        command.CommandText = "DROP TRIGGER final_probe";

        await command.ExecuteNonQueryAsync();

        Assert.True((await AdvanceAsync(connection, execution)).StepComplete);

        Assert.Equal(33, await GrimoireCoreSchemaVersion.ReadAsync(connection, CancellationToken.None));

        Assert.Null(await JournalAsync(connection));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT ClaimCount FROM installation_turn_quota_state"));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("row-mismatch")]
    public async Task A_fresh_execution_refuses_invalid_persisted_census_evidence_before_restarting(string failure)
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await SeedCore32Async(connection, 129);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        using (GrimoireSchemaQuiescentExecution first = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness()))
        {
            await AdvanceAsync(connection, first);
        }

        await using SqliteCommand corrupt = connection.CreateCommand();

        corrupt.CommandText = failure == "malformed"
            ? "UPDATE grimoire_schema_transitions SET BackfillCursor='v1|2||0|0|1|0|0|2|2|0'"
            : "UPDATE grimoire_schema_transitions SET BackfillRowsProcessed=129";

        await corrupt.ExecuteNonQueryAsync();

        GrimoireSchemaTransitionJournalRow before = (await JournalAsync(connection))!;

        using GrimoireSchemaQuiescentExecution next = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

        await Assert.ThrowsAsync<InvalidOperationException>(() => AdvanceAsync(connection, next));

        Assert.Equal(before, await JournalAsync(connection));

        Assert.Equal(2L, await ScalarAsync(connection, "SELECT ClaimCount FROM installation_turn_quota_state"));
    }

    [Fact]
    public async Task Restart_page_commit_failure_does_not_mark_execution_begun_and_repeated_fresh_executions_rebase_once()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await scratch.OpenAsync(CancellationToken.None);

        await SeedCore32Async(connection, 129);

        await GrimoireSchemaTestInstaller.InstallAsync(connection, 1536, CancellationToken.None);

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        using ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        for (int attempt = 0; attempt < 3; attempt++)
        {
            using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

            if (attempt == 1)
            {
                GrimoireSchemaTransitionJournalRow before = (await JournalAsync(connection))!;

                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText = "CREATE TEMP TRIGGER refuse_restart BEFORE UPDATE ON grimoire_schema_transitions BEGIN SELECT RAISE(ABORT,'restart commit refused'); END";

                await command.ExecuteNonQueryAsync();

                await Assert.ThrowsAsync<SqliteException>(() => AdvanceAsync(connection, execution));

                Assert.Equal(before, await JournalAsync(connection));

                command.CommandText = "DROP TRIGGER refuse_restart";

                await command.ExecuteNonQueryAsync();
            }

            Assert.Equal(128, (await AdvanceAsync(connection, execution)).RowsProcessed);

            Assert.Equal(128, (await JournalAsync(connection))!.BackfillRowsProcessed);

            Assert.Equal(1, (await AdvanceAsync(connection, execution)).RowsProcessed);

            Assert.Equal(129, (await JournalAsync(connection))!.BackfillRowsProcessed);
        }

        using GrimoireSchemaQuiescentExecution final = GrimoireSchemaQuiescentExecution.ForStartup(connection, held, root, new GrimoireDbReadiness());

        GrimoireSchemaBackfillProgress completed = await new GrimoireSchemaBackfillRunner(GrimoireSchemaTestInstaller.Create(), TimeProvider.System).AdvanceAsync(
            connection, GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core), (await JournalAsync(connection))!,
            GrimoireSchemaTestInstaller.CreateContext(), 16, CancellationToken.None, final);

        Assert.True(completed.StepComplete);

        Assert.Equal(258, completed.RowsProcessed);

        Assert.Null(await JournalAsync(connection));

        Assert.Equal(1L, await ScalarAsync(connection, "SELECT ClaimCount FROM installation_turn_quota_state"));
    }

    [Theory]
    [InlineData(null, -1)]
    [InlineData("v1|1|00000000-0000-0000-0000-000000000001|1|0|0|0|0|2|2|0", 2)]
    public void Restart_validation_refuses_negative_or_inconsistent_journal_progress(string? cursor, long rows)
    {
        Assert.Throws<InvalidOperationException>(() => new InstallationTurnQuotaBackfill().ValidateRestartCursor(cursor, rows));
    }

    [Fact]
    public async Task Empty_owner_census_restarts_after_its_zero_row_phase_commits()
    {
        using EvolutionScratchDatabase scratch = EvolutionScratchDatabase.Create();

        string root = Path.GetDirectoryName(new SqliteConnectionStringBuilder(scratch.ConnectionString).DataSource)!;

        using (ArcanumMaintenanceLock held = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock())
        await using (SqliteConnection first = await scratch.OpenAsync(CancellationToken.None))
        {
            await SeedCore32Async(first, 0);

            await GrimoireSchemaTestInstaller.InstallAsync(first, 1536, CancellationToken.None);

            using GrimoireSchemaQuiescentExecution execution = GrimoireSchemaQuiescentExecution.ForStartup(first, held, root, new GrimoireDbReadiness());

            GrimoireSchemaBackfillProgress phase = await AdvanceAsync(first, execution);

            Assert.Equal(0, phase.RowsProcessed);

            Assert.False(phase.StepComplete);

            Assert.StartsWith("v1|2|", (await JournalAsync(first))!.BackfillCursor, StringComparison.Ordinal);
        }

        using ArcanumMaintenanceLock resumedLock = ArcanumMaintenanceLock.AcquireDetailed(root).BorrowAcquiredLock();

        await using SqliteConnection resumed = await scratch.OpenAsync(CancellationToken.None);

        using GrimoireSchemaQuiescentExecution resumedExecution = GrimoireSchemaQuiescentExecution.ForStartup(resumed, resumedLock, root, new GrimoireDbReadiness());

        GrimoireSchemaBackfillProgress final = await new GrimoireSchemaBackfillRunner(GrimoireSchemaTestInstaller.Create(), TimeProvider.System).AdvanceAsync(
            resumed, GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core), (await JournalAsync(resumed))!,
            GrimoireSchemaTestInstaller.CreateContext(), 16, CancellationToken.None, resumedExecution);

        Assert.True(final.StepComplete);

        Assert.Equal(0, final.RowsProcessed);

        Assert.Equal(2, final.BatchesRun);

        Assert.Equal(33, await GrimoireCoreSchemaVersion.ReadAsync(resumed, CancellationToken.None));

        Assert.Null(await JournalAsync(resumed));

        Assert.Equal(0L, await ScalarAsync(resumed, "SELECT ClaimCount FROM installation_turn_quota_state"));
    }

    private static async Task SeedCore32Async(SqliteConnection connection, int owners)
    {
        Assert.Equal(CoreSchemaVersionThirtyTwoFixture.PublishedFingerprint, CoreSchemaVersionThirtyTwoFixture.Fingerprint);

        Assert.True((await GrimoireSchemaTestInstaller.InstallAsync(connection,
            CoreSchemaVersionThirtyTwoFixture.ChainSet(), 1536, CancellationToken.None)).Core.IsHealthy);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "INSERT INTO Sessions(Id,Status,CreatedAt,UpdatedAt) VALUES (@id,'archived','2026-01-01T00:00:00.0000000Z','2026-01-01T00:00:00.0000000Z')";

        SqliteParameter id = command.Parameters.Add("@id", SqliteType.Text);

        for (int i = 1; i <= owners; i++)
        {
            id.Value = $"00000000-0000-0000-0000-{i:X12}";

            await command.ExecuteNonQueryAsync();
        }

        command.Parameters.Clear();

        using CovenantSqliteAuthorizationScope quota = CovenantSqliteConnectionInitializer.Instance.Authorize(connection,
            CovenantSqliteAuthorizationKind.TurnCapacityMutation);

        command.CommandText = """
            UPDATE session_turn_quota_state SET ClaimCount=1,ReservedFinalizationCount=1 WHERE SessionId='00000000-0000-0000-0000-000000000001';
            UPDATE installation_turn_quota_state SET ClaimCount=1,ReservedFinalizationCount=1;
            UPDATE installation_turn_quota_state SET ClaimCount=2,ReservedFinalizationCount=2;
            """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<GrimoireSchemaBackfillProgress> AdvanceAsync(SqliteConnection connection,
        GrimoireSchemaQuiescentExecution execution, CancellationToken cancellationToken = default) =>
        await new GrimoireSchemaBackfillRunner(GrimoireSchemaTestInstaller.Create(), TimeProvider.System).AdvanceAsync(connection,
            GrimoireSchemaVersionChains.Default.ForTier(GrimoireSchemaTransactionTier.Core), (await JournalAsync(connection))!,
            GrimoireSchemaTestInstaller.CreateContext(), 1, cancellationToken, execution);

    private static Task<GrimoireSchemaTransitionJournalRow?> JournalAsync(SqliteConnection connection) =>
        GrimoireSchemaTransitionJournal.ReadAsync(connection, null, GrimoireSchemaTransactionTier.Core, CancellationToken.None);

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }
}
