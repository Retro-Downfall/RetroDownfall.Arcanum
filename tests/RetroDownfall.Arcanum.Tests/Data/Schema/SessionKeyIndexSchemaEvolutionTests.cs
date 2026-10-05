using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core version 14: the session-key expression indexes on <c>attachment_memory_consultations</c>,
/// <c>saga_extraction_watermarks</c> and <c>SessionContextPins</c>, reached from a genuine version-13
/// installation the way every installation reaches them on its next open.
/// </summary>
/// <remarks>
/// Each case installs the version-13 catalog this binary's fixture freezes, writes rows into the three
/// tables, and then opens it at the head, so the version-14 step runs over tables that already hold data
/// and every later step runs after it.
/// </remarks>
public sealed class SessionKeyIndexSchemaEvolutionTests
{
    private const string SessionId = "5A6B7C8D-9E0F-4A1B-8C2D-3E4F5A6B7C8D";

    private const string EntryId = "6B7C8D9E-0F1A-4B2C-9D3E-4F5A6B7C8D9E";

    private const string AttachmentId = "7C8D9E0F-1A2B-4C3D-8E4F-5A6B7C8D9E0F";

    private static readonly string[] SessionKeyIndexes =
    [
        "IX_attachment_memory_consultations_SessionId_Norm",
        "IX_saga_extraction_watermarks_SessionId_Norm",
        "IX_SessionContextPins_SessionId_Norm",
    ];

    static SessionKeyIndexSchemaEvolutionTests() => SqliteNativeRuntime.Instance.Initialize();

    [Fact]
    public async Task A_version_thirteen_installation_with_session_keyed_rows_evolves_to_the_fresh_head()
    {
        IReadOnlyDictionary<string, string> fresh = await DefinitionsAsync(evolve: false);

        IReadOnlyDictionary<string, string> evolved = await DefinitionsAsync(evolve: true);

        foreach (string index in SessionKeyIndexes)
        {
            Assert.Contains(index, fresh.Keys);
        }

        Assert.Equal(fresh.Keys.Order(StringComparer.Ordinal), evolved.Keys.Order(StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {
            Assert.Equal(definition, evolved[name]);
        }
    }

    [Fact]
    public async Task The_session_keyed_rows_survive_the_upgrade_and_are_found_through_the_new_indexes()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionThirteenFixture.ChainSet(), 13);

        foreach (string index in SessionKeyIndexes)
        {
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM sqlite_master WHERE name = '{index}';"));
        }

        await SeedSessionKeyedRowsAsync(connection);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        string key = SessionId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

        foreach ((string table, string index) in SessionKeyIndexes.Select(static index => (TableOf(index), index)))
        {
            Assert.Equal(
                1L,
                await ScalarAsync(connection, $"SELECT count(*) FROM {table} WHERE lower(replace(SessionId, '-', '')) = '{key}';"));

            Assert.Equal(
                $"SEARCH {table} USING INDEX {index} (<expr>=?)",
                await ExplainAsync(connection, $"DELETE FROM {table} WHERE lower(replace(SessionId, '-', '')) = @id"));
        }
    }

    private static string TableOf(string index) =>
        index["IX_".Length..index.IndexOf("_SessionId_Norm", StringComparison.Ordinal)];

    /// <summary>
    /// One row in each of the three tables for one Session, each in the spelling its writer uses, with the
    /// Session and Entry the foreign keys require.
    /// </summary>
    private static async Task SeedSessionKeyedRowsAsync(SqliteConnection connection)
    {
        string at = UtcInstantText.Format(DateTimeOffset.UtcNow);

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Sessions" ("Id", "Title", "Status", "CreatedAt", "UpdatedAt")
            VALUES ($session, 'upgrade', 'active', $at, $at);
            """,
            ("$session", SessionId),
            ("$at", at));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "Entries" ("Id", "SessionId", "Role", "Content", "ModelUsed", "CreatedAt", "Sequence")
            VALUES ($entry, $session, 0, 'hello', 'model', $at, 1);
            """,
            ("$entry", EntryId),
            ("$session", SessionId),
            ("$at", at));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO attachment_memory_consultations
                (SourceEntryId, SessionId, AttachmentId, LogicalKey, Version, ContentHash, MaterializedAt, SourceType)
            VALUES ($entry, $sessionLower, $attachment, 'key', 1, 'hash', $at, 'file');
            """,
            ("$entry", EntryId),
            ("$attachment", AttachmentId),
            ("$sessionLower", SessionId.ToLowerInvariant()),
            ("$at", at));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO saga_extraction_watermarks (SessionId, LastExtractedEntryCreatedAt, LastExtractedEntrySequence)
            VALUES ($sessionLower, $at, 1);
            """,
            ("$sessionLower", SessionId.ToLowerInvariant()),
            ("$at", at));

        await ExecuteAsync(
            connection,
            """
            INSERT INTO "SessionContextPins"
                ("Id", "SessionId", "Kind", "TargetIdentifier", "DisplayLabel", "CreatedAt", "UpdatedAt")
            VALUES ($pin, $session, 0, 'target', 'label', $at, $at);
            """,
            ("$pin", Guid.NewGuid().ToString("D").ToUpperInvariant()),
            ("$session", SessionId),
            ("$at", at));
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, chains, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
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

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ExplainAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "EXPLAIN QUERY PLAN " + sql;

        _ = command.Parameters.AddWithValue("@id", "00000000000000000000000000000000");

        List<string> rows = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(reader.GetOrdinal("detail")));
        }

        return string.Join("\n", rows);
    }

    private static async Task<IReadOnlyDictionary<string, string>> DefinitionsAsync(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {
            await InstallAsync(connection, CoreSchemaVersionThirteenFixture.ChainSet(), 13);

            await SeedSessionKeyedRowsAsync(connection);
        }

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Dictionary<string, string> definitions = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%';";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            definitions.Add(reader.GetString(0), GrimoireSqlNormalizer.Normalize(reader.GetString(1)));
        }

        return definitions;
    }
}
