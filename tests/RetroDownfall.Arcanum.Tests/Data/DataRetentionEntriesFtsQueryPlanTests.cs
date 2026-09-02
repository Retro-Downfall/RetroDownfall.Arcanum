using System.Data;

using Microsoft.Data.Sqlite;

using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Infrastructure.Data;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The pinned <c>EXPLAIN QUERY PLAN</c> of every retention probe of <c>Entries_fts</c>.
/// </summary>
/// <remarks>
/// <c>Entries_fts</c> is a standalone FTS5 table whose <c>Id</c>, <c>SessionId</c> and <c>Role</c>
/// columns are UNINDEXED, so rowid is the only key FTS5 can resolve without walking the whole
/// content index. <c>Entries_ad</c> says so in its own comment and keys its delete by rowid because
/// <c>WHERE Id = old.Id</c> "makes a session purge or an entry-retention sweep quadratic" — and the
/// retention sweep then issued exactly that shape anyway, once per candidate, for a row the trigger
/// had already removed.
///
/// <para>FTS5 answers <c>SCAN</c> for every access, so the verb decides nothing here. What decides is
/// the index string SQLite prints after it: a bare <c>INDEX 0:</c> means no constraint reached the
/// virtual table and it walks the index, while <c>INDEX 0:=</c> means a rowid equality was handed
/// down and it looks the one row up. The plan is pinned whole rather than searched for a word, so a
/// predicate that silently stopped being rowid-keyed reds here.</para>
///
/// <para>The predicate is taken from the constant the service executes rather than reassembled, for
/// the reason <c>EntryTemporalQueryPlanTests</c> gives: a plan test that explained SQL of its own
/// would keep passing after the real probe quietly lost its key.</para>
/// </remarks>
[Collection("Grimoire")]

[Trait("Category", "Integration")]

public sealed class DataRetentionEntriesFtsQueryPlanTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public DataRetentionEntriesFtsQueryPlanTests(GrimoireFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {

        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;

    }

    public async Task DisposeAsync()
    {

        if (_db is not null)
        {

            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

            await _db.DisposeAsync();

            SqliteConnection.ClearPool(connection);

        }

        if (File.Exists(_dbPath))
        {

            File.Delete(_dbPath);

        }

    }

    [SkippableFact]

    public async Task The_retention_probe_of_the_entry_index_is_answered_by_rowid()
    {

        RequireSqlCipher();

        string plan = await ExplainAsync(
            $"SELECT COUNT(*) FROM \"Entries_fts\" WHERE {DataRetentionService.EntryFtsProbePredicate}");

        Assert.Equal("SCAN Entries_fts VIRTUAL TABLE INDEX 0:=", plan);

    }

    private async Task<string> ExplainAsync(string sql)
    {

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State is not ConnectionState.Open)
        {

            await connection.OpenAsync(CancellationToken.None);

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        // Both spellings are bound whatever the predicate names, so flipping the probe from the
        // identity column to the rowid does not turn a plan regression into a binding error.
        _ = command.Parameters.AddWithValue("@id", "00000000000000000000000000000000");

        _ = command.Parameters.AddWithValue("@rowid", 1L);

        List<string> rows = [];

        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None))
        {

            while (await reader.ReadAsync(CancellationToken.None))
            {

                rows.Add(reader.GetString(reader.GetOrdinal("detail")));

            }

        }

        return string.Join("\n", rows);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(
            GrimoireFixture.SqlCipherAvailable,
            GrimoireFixture.SqlCipherUnavailableReason);

}
