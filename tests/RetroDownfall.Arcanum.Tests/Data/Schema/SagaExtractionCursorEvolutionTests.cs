using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// The version-7 upgrade from Saga's timestamp-only extraction watermark to its exact entry cursor.
/// </summary>
public sealed class SagaExtractionCursorEvolutionTests
{
    static SagaExtractionCursorEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_current_installation_refuses_a_timestamp_only_cursor_insert()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult installed = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, installed.Core.Health);

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => ExecuteAsync(
                connection,
                """
                INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt)
                VALUES ('timestamp-only', '2026-08-02T00:00:00.0000000+00:00');
                """));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Timestamp_watermarks_backfill_to_the_last_contiguous_paid_sequence_without_losing_tick_precision()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult seeded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSixFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, seeded.Core.Health);

        Assert.Equal(6, seeded.Core.SchemaVersion);

        Guid sessionId = new("A0000000-0000-4000-8000-000000000255");

        DateTimeOffset before = DateTimeOffset.Parse("2026-08-01T00:00:00Z", CultureInfo.InvariantCulture);

        DateTimeOffset watermark = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);

        DateTimeOffset after = watermark.AddTicks(1);

        await SeedSessionAsync(connection, sessionId, before, after);

        await SeedEntryAsync(connection, sessionId, sequence: 40, before);

        await SeedEntryAsync(connection, sessionId, sequence: 41, watermark);

        await SeedEntryAsync(connection, sessionId, sequence: 42, watermark);

        await SeedEntryAsync(connection, sessionId, sequence: 43, after);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt)
            VALUES ($sessionId, $watermark);
            """,
            ("$sessionId", sessionId.ToString()),
            ("$watermark", watermark.ToString("o", CultureInfo.InvariantCulture)));

        Guid inheritedFinalizationId = new("A1000000-0000-4000-8000-000000000255");

        using (CovenantSqliteAuthorizationScope capacity = CovenantSqliteConnectionInitializer
            .Instance
            .Authorize(connection, CovenantSqliteAuthorizationKind.TurnCapacityMutation))
        {
            await ExecuteAsync(
                connection,
                """
                INSERT INTO assistant_finalization_capacity_reservations (
                    ReservationId,
                    SessionId,
                    AssistantEntryId,
                    OriginCode,
                    ClaimId,
                    StateCode,
                    CreatedAtUtc,
                    StateChangedAtUtc)
                VALUES (
                    $reservationId,
                    $sessionId,
                    $assistantEntryId,
                    2,
                    NULL,
                    2,
                    $createdAtUtc,
                    $createdAtUtc);
                """,
                ("$reservationId", Guid.NewGuid().ToString()),
                ("$sessionId", sessionId.ToString("D").ToUpperInvariant()),
                ("$assistantEntryId", inheritedFinalizationId.ToString("D").ToUpperInvariant()),
                ("$createdAtUtc", watermark.ToString("o", CultureInfo.InvariantCulture)));
        }

        await ExecuteAsync(
            connection,
            """
            INSERT INTO assistant_entry_finalizations (
                AssistantEntryId,
                SessionId,
                OutcomeCode,
                ContentSensitivityCode,
                ContentSensitivityDigest,
                RequestDigest,
                FinalReceiptDigest,
                SourceEvidenceDigest,
                FinalizedAtUtc)
            VALUES (
                $assistantEntryId,
                $sessionId,
                1,
                0,
                zeroblob(32),
                zeroblob(32),
                NULL,
                NULL,
                $finalizedAtUtc);
            """,
            ("$assistantEntryId", inheritedFinalizationId.ToString("D").ToUpperInvariant()),
            ("$sessionId", sessionId.ToString("D").ToUpperInvariant()),
            ("$finalizedAtUtc", watermark.ToString("o", CultureInfo.InvariantCulture)));

        Guid nonMonotonicSessionId = new("B0000000-0000-4000-8000-000000000255");

        await SeedSessionAsync(connection, nonMonotonicSessionId, before, after);

        await SeedEntryAsync(connection, nonMonotonicSessionId, sequence: 1, after);

        await SeedEntryAsync(connection, nonMonotonicSessionId, sequence: 2, before);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt)
            VALUES ($sessionId, $watermark);
            """,
            ("$sessionId", nonMonotonicSessionId.ToString()),
            ("$watermark", watermark.ToString("o", CultureInfo.InvariantCulture)));

        Guid emptySessionId = new("C0000000-0000-4000-8000-000000000255");

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt)
            VALUES ($sessionId, $watermark);
            """,
            ("$sessionId", emptySessionId.ToString()),
            ("$watermark", watermark.ToString("o", CultureInfo.InvariantCulture)));

        GrimoireSchemaInstallResult evolved = await UpgradeToHeadAsync(
            connection,
            expectedUnresolvedCursorCount: 3);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, evolved.Core.Health);

        Assert.Equal(7, evolved.Core.SchemaVersion);

        Assert.Equal(
            42L,
            await ScalarInt64Async(
                connection,
                "SELECT LastExtractedEntrySequence FROM saga_extraction_watermarks WHERE SessionId = $sessionId;",
                ("$sessionId", sessionId.ToString())));

        Assert.Equal(
            1L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM assistant_entry_finalizations WHERE AssistantEntryId = $assistantEntryId;",
                ("$assistantEntryId", inheritedFinalizationId.ToString("D").ToUpperInvariant())));

        Assert.Equal(
            DBNull.Value,
            await ScalarAsync(
                connection,
                "SELECT ThroughEntrySequence FROM assistant_entry_finalizations WHERE AssistantEntryId = $assistantEntryId;",
                ("$assistantEntryId", inheritedFinalizationId.ToString("D").ToUpperInvariant())));

        Assert.Equal(
            watermark.ToString("o", CultureInfo.InvariantCulture),
            await ScalarStringAsync(
                connection,
                "SELECT LastExtractedEntryCreatedAt FROM saga_extraction_watermarks WHERE SessionId = $sessionId;",
                ("$sessionId", sessionId.ToString())));

        Assert.Equal(
            0L,
            await ScalarInt64Async(
                connection,
                "SELECT LastExtractedEntrySequence FROM saga_extraction_watermarks WHERE SessionId = $sessionId;",
                ("$sessionId", emptySessionId.ToString())));

        Assert.Equal(
            0L,
            await ScalarInt64Async(
                connection,
                "SELECT LastExtractedEntrySequence FROM saga_extraction_watermarks WHERE SessionId = $sessionId;",
                ("$sessionId", nonMonotonicSessionId.ToString())));

        Assert.Equal(
            2L,
            await ScalarInt64Async(
                connection,
                """
                SELECT COUNT(*)
                FROM "Entries" AS entry
                JOIN saga_extraction_watermarks AS watermark
                  ON lower(replace(entry."SessionId", '-', '')) = lower(replace(watermark.SessionId, '-', ''))
                WHERE watermark.SessionId = $sessionId
                  AND entry.Sequence > watermark.LastExtractedEntrySequence;
                """,
                ("$sessionId", nonMonotonicSessionId.ToString())));

        Assert.Equal(
            43L,
            await ScalarInt64Async(
                connection,
                """
                SELECT MIN(entry.Sequence)
                FROM "Entries" AS entry
                JOIN saga_extraction_watermarks AS watermark
                  ON lower(replace(entry."SessionId", '-', '')) = lower(replace(watermark.SessionId, '-', ''))
                WHERE watermark.SessionId = $sessionId
                  AND entry.Sequence > watermark.LastExtractedEntrySequence;
                """,
                ("$sessionId", sessionId.ToString())));

        GrimoireSchemaInstallResult converged = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSevenFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.Core.Health);

        Assert.Equal(7, converged.Core.SchemaVersion);
    }

    [Fact]
    public async Task A_large_watermark_is_checkpointed_and_resumed_across_bounded_transition_passes()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        GrimoireSchemaInstallResult seeded = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSixFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, seeded.Core.Health);

        Guid sessionId = new("D0000000-0000-4000-8000-000000000255");

        DateTimeOffset watermark = DateTimeOffset.Parse("2026-08-02T00:00:00Z", CultureInfo.InvariantCulture);

        DateTimeOffset after = watermark.AddTicks(1);

        await SeedSessionAsync(connection, sessionId, watermark.AddDays(-1), after);

        await SeedEntryRangeAsync(connection, sessionId, count: 3300, watermark);

        await SeedEntryAsync(connection, sessionId, sequence: 3301, after);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt)
            VALUES ($sessionId, $watermark);
            """,
            ("$sessionId", sessionId.ToString()),
            ("$watermark", watermark.ToString("o", CultureInfo.InvariantCulture)));

        GrimoireSchemaVersionChainSet target = CoreSchemaVersionSevenFixture.ChainSet();

        GrimoireSchemaInstallResult staged = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            target,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, staged.Core.Health);

        ServiceCollection collection = new();

        _ = collection.AddOptions();

        _ = collection.Configure<ArcanumSettings>(static _ => { });

        await using ServiceProvider services = collection.BuildServiceProvider();

        GrimoireSchemaInstaller installer =
            GrimoireSchemaTestInstaller.Create(target);

        GrimoireSchemaTransitionCoordinator coordinator = new(
            new FixedCoreConnectionSource(connection),
            target,
            installer,
            new GrimoireSchemaBackfillRunner(installer, TimeProvider.System),
            services,
            timeProvider: TimeProvider.System);

        GrimoireSchemaTransitionPassOutcome firstPass =
            (await coordinator.RunOnceAsync(CancellationToken.None)).Value;

        Assert.Equal(GrimoireSchemaTransitionCoordinator.MaxBatchesPerPass, firstPass.BatchesRun);

        Assert.InRange(firstPass.RowsProcessed, 1, 3200);

        Assert.Equal(
            DBNull.Value,
            await ScalarAsync(
                connection,
                "SELECT LastExtractedEntrySequence FROM saga_extraction_watermarks WHERE SessionId = $sessionId;",
                ("$sessionId", sessionId.ToString())));

        GrimoireSchemaTransitionJournalRow journal = Assert.IsType<GrimoireSchemaTransitionJournalRow>(
            await GrimoireSchemaTransitionJournal.ReadAsync(
                connection,
                transaction: null,
                GrimoireSchemaTransactionTier.Core,
                CancellationToken.None));

        Assert.NotNull(journal.BackfillCursor);

        GrimoireSchemaTransitionPassOutcome secondPass =
            (await coordinator.RunOnceAsync(CancellationToken.None)).Value;

        Assert.True(secondPass.Advanced);

        Assert.Equal(
            3300L,
            await ScalarInt64Async(
                connection,
                "SELECT LastExtractedEntrySequence FROM saga_extraction_watermarks WHERE SessionId = $sessionId;",
                ("$sessionId", sessionId.ToString())));

        Assert.Null(
            await GrimoireSchemaTransitionJournal.ReadAsync(
                connection,
                transaction: null,
                GrimoireSchemaTransactionTier.Core,
                CancellationToken.None));

        GrimoireSchemaInstallResult converged = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            target,
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, converged.Core.Health);

        Assert.Equal(7, converged.Core.SchemaVersion);
    }

    private static async Task<GrimoireSchemaInstallResult> UpgradeToHeadAsync(
        SqliteConnection connection,
        long expectedUnresolvedCursorCount)
    {
        GrimoireSchemaInstallResult staged = await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            CoreSchemaVersionSevenFixture.ChainSet(),
            1536,
            CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.TransitionIncomplete, staged.Core.Health);

        Assert.Equal(
            expectedUnresolvedCursorCount,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM saga_extraction_watermarks WHERE LastExtractedEntrySequence IS NULL;"));

        ServiceCollection collection = new();

        _ = collection.AddOptions();

        _ = collection.Configure<ArcanumSettings>(static _ => { });

        await using ServiceProvider services = collection.BuildServiceProvider();

        GrimoireSchemaVersionChainSet target = CoreSchemaVersionSevenFixture.ChainSet();

        GrimoireSchemaInstaller installer =
            GrimoireSchemaTestInstaller.Create(target);

        GrimoireSchemaTransitionCoordinator coordinator = new(
            new FixedCoreConnectionSource(connection),
            target,
            installer,
            new GrimoireSchemaBackfillRunner(installer, TimeProvider.System),
            services,
            timeProvider: TimeProvider.System);

        for (int pass = 0; pass < 4; pass++)
        {
            if (!(await coordinator.RunOnceAsync(CancellationToken.None)).Value.Advanced)
            {
                break;
            }
        }

        Assert.Equal(
            0L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM saga_extraction_watermarks WHERE LastExtractedEntrySequence IS NULL;"));

        string expectedDefinition = CoreSchemaVersionSevenFixture.Objects
            .Single(static definition => definition.Name == "saga_extraction_watermarks")
            .Sql;

        string actualDefinition = await ScalarStringAsync(
            connection,
            "SELECT sql FROM sqlite_schema WHERE type = 'table' AND name = 'saga_extraction_watermarks';");

        Assert.Equal(
            GrimoireSqlNormalizer.Normalize(expectedDefinition),
            GrimoireSqlNormalizer.Normalize(actualDefinition));

        return await GrimoireSchemaTestInstaller.InstallAsync(
            connection,
            target,
            1536,
            CancellationToken.None);
    }

    private static Task SeedSessionAsync(
        SqliteConnection connection,
        Guid sessionId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($id, 'active', $createdAt, $updatedAt);
            """,
            ("$id", sessionId.ToString("D").ToUpperInvariant()),
            ("$createdAt", createdAt.ToString("o", CultureInfo.InvariantCulture)),
            ("$updatedAt", updatedAt.ToString("o", CultureInfo.InvariantCulture)));

    private static Task SeedEntryAsync(
        SqliteConnection connection,
        Guid sessionId,
        long sequence,
        DateTimeOffset createdAt) =>
        ExecuteAsync(
            connection,
            """
            INSERT INTO "Entries"
                ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
            VALUES ($id, $sessionId, 0, $content, 'test-model', $createdAt, $sequence);
            """,
            ("$id", Guid.NewGuid().ToString("D").ToUpperInvariant()),
            ("$sessionId", sessionId.ToString("D").ToUpperInvariant()),
            ("$content", $"entry {sequence}"),
            ("$createdAt", createdAt.ToString("o", CultureInfo.InvariantCulture)),
            ("$sequence", sequence));

    private static async Task SeedEntryRangeAsync(
        SqliteConnection connection,
        Guid sessionId,
        int count,
        DateTimeOffset createdAt)
    {
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO "Entries"
                ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
            VALUES ($id, $sessionId, 0, $content, 'test-model', $createdAt, $sequence);
            """;

        SqliteParameter id = command.Parameters.Add("$id", SqliteType.Text);

        _ = command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D").ToUpperInvariant());

        SqliteParameter content = command.Parameters.Add("$content", SqliteType.Text);

        _ = command.Parameters.AddWithValue(
            "$createdAt",
            createdAt.ToString("o", CultureInfo.InvariantCulture));

        SqliteParameter sequence = command.Parameters.Add("$sequence", SqliteType.Integer);

        for (int value = 1; value <= count; value++)
        {
            id.Value = Guid.NewGuid().ToString("D").ToUpperInvariant();

            content.Value = $"entry {value}";

            sequence.Value = value;

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        object? value = await ScalarAsync(connection, sql, parameters).ConfigureAwait(false);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        object? value = await ScalarAsync(connection, sql, parameters).ConfigureAwait(false);

        return Assert.IsType<string>(value);
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            _ = command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class FixedCoreConnectionSource(SqliteConnection connection) : ICovenantConnectionSource
    {
        public ValueTask<SqliteConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);

        public ValueTask<SqliteConnection> GetOpenCoreConnectionAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(connection);
    }
}
