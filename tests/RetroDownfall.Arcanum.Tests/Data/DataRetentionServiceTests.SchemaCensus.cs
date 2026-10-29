using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{
    [SkippableTheory]
    [InlineData("table")]
    [InlineData("view")]
    [InlineData("virtual")]
    public async Task SchemaCensus_observes_external_DDL_between_lookups(string shape)
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await using var independent = _fixture.CreateContext(_dbPath);

        await independent.Database.OpenConnectionAsync();

        SqliteConnection other = (SqliteConnection)independent.Database.GetDbConnection();

        using RetentionSchemaCensus census = new();

        Assert.False(await census.ExistsAsync(connection, "rv237_probe_vec", CancellationToken.None));

        string create = shape switch
        {
            "view" => "CREATE VIEW rv237_probe_vec AS SELECT 1 AS Id",
            "virtual" => "CREATE VIRTUAL TABLE rv237_probe_vec USING fts5(Content)",
            _ => "CREATE TABLE rv237_probe_vec (Id TEXT)",
        };

        await ExecuteCensusSqlAsync(other, create);

        Assert.Equal(shape != "virtual", await census.ExistsAsync(connection, "rv237_probe_vec", CancellationToken.None));

        await ExecuteCensusSqlAsync(other, shape == "view" ? "DROP VIEW rv237_probe_vec" : "DROP TABLE rv237_probe_vec");

        Assert.False(await census.ExistsAsync(connection, "rv237_probe_vec", CancellationToken.None));
    }

    [SkippableFact]
    public async Task SchemaCensus_invalidates_on_connection_reopen_even_with_the_same_schema_cookie()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await ExecuteCensusSqlAsync(connection, "CREATE TABLE rv237_first (Id TEXT)");

        using RetentionSchemaCensus census = new();

        Assert.True(await census.ExistsAsync(connection, "rv237_first", CancellationToken.None));

        await using SqliteCommand version = connection.CreateCommand();

        version.CommandText = "PRAGMA schema_version";

        long cookie = (long)(await version.ExecuteScalarAsync())!;

        await connection.CloseAsync();

        await using var independent = _fixture.CreateContext(_dbPath);

        await independent.Database.OpenConnectionAsync();

        SqliteConnection other = (SqliteConnection)independent.Database.GetDbConnection();

        // Deliberately restore the cookie to prove that reopening invalidates the old census,
        // rather than merely relying on normal DDL's cookie increment.
        await ExecuteCensusSqlAsync(other, $"DROP TABLE rv237_first; CREATE TABLE rv237_second (Id TEXT); PRAGMA schema_version = {cookie}");

        await connection.OpenAsync();

        Assert.False(await census.ExistsAsync(connection, "rv237_first", CancellationToken.None));

        Assert.True(await census.ExistsAsync(connection, "rv237_second", CancellationToken.None));
    }

    [SkippableFact]
    public async Task SchemaCensus_tracks_transaction_local_DDL_and_rollback()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        using RetentionSchemaCensus census = new();

        Assert.False(await census.ExistsAsync(connection, "rv237_probe", CancellationToken.None));

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            await ExecuteCensusSqlAsync(connection, "CREATE TABLE rv237_probe (Id TEXT)", transaction);

            Assert.True(await census.ExistsAsync(connection, "rv237_probe", CancellationToken.None));

            await transaction.RollbackAsync();
        }

        Assert.False(await census.ExistsAsync(connection, "rv237_probe", CancellationToken.None));
    }

    [SkippableFact]
    public async Task SchemaCensus_never_opens_an_unavailable_legacy_vector_module()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        await ExecuteCensusSqlAsync(connection,
            """
            CREATE TABLE rv237_probe_vec (Id TEXT);
            DROP TABLE rv237_probe_vec;
            PRAGMA writable_schema = ON;
            INSERT INTO sqlite_schema(type, name, tbl_name, rootpage, sql)
            VALUES ('table', 'rv237_probe_vec', 'rv237_probe_vec', 0,
                    'CREATE VIRTUAL TABLE rv237_probe_vec USING vec0(Id TEXT PRIMARY KEY, Embedding FLOAT[768])');
            PRAGMA writable_schema = RESET;
            """);

        using RetentionSchemaCensus census = new();

        Assert.False(await census.ExistsAsync(connection, "rv237_probe_vec", CancellationToken.None));

        SqliteException error = await Assert.ThrowsAsync<SqliteException>(() =>
            ExecuteCensusSqlAsync(connection, "SELECT COUNT(*) FROM rv237_probe_vec"));

        Assert.Contains("no such module: vec0", error.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SchemaCensus_honors_cancellation_after_warming_metadata()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        using RetentionSchemaCensus census = new();

        Assert.True(await census.ExistsAsync(connection, "Sessions", CancellationToken.None));

        using CancellationTokenSource cancelled = new();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => census.ExistsAsync(connection, "Sessions", cancelled.Token));
    }

    [SkippableFact]
    public async Task SchemaCensus_rejects_shared_provenance_made_partial_after_warming()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        using RetentionSchemaCensus census = new();

        Assert.True(await CampaignRollupSourceStorage.IsInstalledAsync(connection, null, CancellationToken.None, census));

        await using var independent = _fixture.CreateContext(_dbPath);

        await independent.Database.OpenConnectionAsync();

        await ExecuteCensusSqlAsync((SqliteConnection)independent.Database.GetDbConnection(), "DROP VIEW campaign_rollup_source_membership");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CampaignRollupSourceStorage.IsInstalledAsync(connection, null, CancellationToken.None, census));
    }

    [SkippableFact]
    public async Task SchemaCensus_distinguishes_new_transactions_reusing_a_rolled_back_schema_cookie()
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        using RetentionSchemaCensus census = new();

        await using (SqliteTransaction first = connection.BeginTransaction())
        {
            await ExecuteCensusSqlAsync(connection, "CREATE TABLE rv237_first (Id TEXT)", first);

            Assert.True(await census.ExistsAsync(connection, "rv237_first", CancellationToken.None));

            await first.RollbackAsync();
        }

        await using (SqliteTransaction second = connection.BeginTransaction())
        {
            await ExecuteCensusSqlAsync(connection, "CREATE TABLE rv237_second (Id TEXT)", second);

            Assert.False(await census.ExistsAsync(connection, "rv237_first", CancellationToken.None));

            Assert.True(await census.ExistsAsync(connection, "rv237_second", CancellationToken.None));

            await second.RollbackAsync();
        }
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SchemaCensus_does_not_reuse_metadata_across_savepoint_rollback(bool scalar, bool nativeTransaction)
    {
        RequireSqlCipher();

        await _db!.Database.OpenConnectionAsync();

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        using RetentionSchemaCensus census = new();

        await using SqliteTransaction? transaction = nativeTransaction ? null : connection.BeginTransaction();

        if (nativeTransaction) await ExecuteCensusSqlAsync(connection, "BEGIN");

        await using SqliteCommand query = connection.CreateCommand();

        query.Transaction = transaction;

        query.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name = 'rv237_first')";

        await ExecuteCensusSqlAsync(connection, "SAVEPOINT probe; CREATE TABLE rv237_first (Id TEXT)", transaction);

        Assert.True(scalar
            ? await census.ReadScalarAsync(query, CancellationToken.None) == 1
            : await census.ExistsAsync(connection, "rv237_first", CancellationToken.None));

        await ExecuteCensusSqlAsync(connection, "ROLLBACK TO probe; CREATE TABLE rv237_second (Id TEXT)", transaction);

        Assert.False(scalar
            ? await census.ReadScalarAsync(query, CancellationToken.None) == 1
            : await census.ExistsAsync(connection, "rv237_first", CancellationToken.None));

        if (transaction is null) await ExecuteCensusSqlAsync(connection, "ROLLBACK");
        else await transaction.RollbackAsync();
    }

    private static async Task ExecuteCensusSqlAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }
}
