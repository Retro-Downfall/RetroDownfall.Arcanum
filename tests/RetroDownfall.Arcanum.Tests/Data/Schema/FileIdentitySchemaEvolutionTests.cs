using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data.Schema;

/// <summary>
/// Core version 15: the file identities (<c>UploadedFiles.Id</c> and the three file roles a batch names) settle on
/// the canonical uppercase dashed spelling, and the batch roles are indexed, reached the same way from a fresh install
/// and from a version-14 installation.
/// </summary>
public sealed class FileIdentitySchemaEvolutionTests
{
    private const string LowerDashed = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";

    private const string LowerPlain = "1b2c3d4e5f6a4b7c8d9e0f1a2b3c4d5e";

    private const string UpperPlain = "2C3D4E5F6A7B4C8D9E0F1A2B3C4D5E6F";

    private const string Canonical = "3D4E5F6A-7B8C-4D9E-8F0A-1B2C3D4E5F60";

    private const string MixedDashed = "4e5f6a7b-8C9D-4E0F-9A1B-2C3D4E5F6071";

    [Fact]
    public async Task Fresh_and_evolved_version_fifteen_catalogs_have_identical_definitions()
    {
        IReadOnlyDictionary<string, string> fresh = await DefinitionsAsync(evolve: false);

        IReadOnlyDictionary<string, string> evolved = await DefinitionsAsync(evolve: true);

        foreach (string name in (string[])["IX_Batches_InputFileId", "IX_Batches_OutputFileId", "IX_Batches_ErrorFileId"])
        {
            Assert.Contains(name, fresh.Keys);
        }

        Assert.Equal(fresh.Keys.Order(StringComparer.Ordinal), evolved.Keys.Order(StringComparer.Ordinal));

        foreach ((string name, string definition) in fresh)
        {
            Assert.Equal(definition, evolved[name]);
        }
    }

    [Fact]
    public async Task Version_fifteen_settles_every_legacy_file_identity_spelling_and_leaves_canonical_rows_alone()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        foreach (string id in (string[])[LowerDashed, LowerPlain, UpperPlain, Canonical, MixedDashed])
        {
            await InsertUploadedFileAsync(connection, id);
        }

        await InsertBatchAsync(connection, "batch-one", input: LowerDashed, output: LowerPlain, error: null);

        await InsertBatchAsync(connection, "batch-two", input: Canonical, output: null, error: MixedDashed);

        await InsertBatchAsync(connection, "batch-three", input: UpperPlain, output: UpperPlain, error: UpperPlain);

        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(
            [
                "0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D",
                "1B2C3D4E-5F6A-4B7C-8D9E-0F1A2B3C4D5E",
                "2C3D4E5F-6A7B-4C8D-9E0F-1A2B3C4D5E6F",
                "3D4E5F6A-7B8C-4D9E-8F0A-1B2C3D4E5F60",
                "4E5F6A7B-8C9D-4E0F-9A1B-2C3D4E5F6071",
            ],
            await ColumnAsync(connection, """SELECT "Id" FROM "UploadedFiles" ORDER BY "Id";"""));

        Assert.Equal(
            ["0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D", "2C3D4E5F-6A7B-4C8D-9E0F-1A2B3C4D5E6F", "3D4E5F6A-7B8C-4D9E-8F0A-1B2C3D4E5F60"],
            await ColumnAsync(connection, """SELECT "InputFileId" FROM "Batches" ORDER BY "Id";"""));

        Assert.Equal(
            ["1B2C3D4E-5F6A-4B7C-8D9E-0F1A2B3C4D5E", "2C3D4E5F-6A7B-4C8D-9E0F-1A2B3C4D5E6F", "<null>"],
            await ColumnAsync(connection, """SELECT "OutputFileId" FROM "Batches" ORDER BY "Id";"""));

        Assert.Equal(
            ["<null>", "2C3D4E5F-6A7B-4C8D-9E0F-1A2B3C4D5E6F", "4E5F6A7B-8C9D-4E0F-9A1B-2C3D4E5F6071"],
            await ColumnAsync(connection, """SELECT "ErrorFileId" FROM "Batches" ORDER BY "Id";"""));

        // A batch's own identity is not a file identity and is not rewritten.
        Assert.Equal(
            ["batch-one", "batch-three", "batch-two"],
            await ColumnAsync(connection, """SELECT "Id" FROM "Batches" ORDER BY "Id";"""));
    }

    [Fact]
    public async Task Version_fifteen_completes_when_two_uploaded_files_differ_only_by_case()
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);

        await InsertUploadedFileAsync(connection, LowerDashed);

        await InsertUploadedFileAsync(connection, LowerDashed.ToUpperInvariant());

        // The second row would collide with the first on the primary key, so the transition must not abort over it.
        await InstallAsync(connection, GrimoireSchemaVersionChains.Default, GrimoireSchemaVersionChains.CoreSchemaVersion);

        Assert.Equal(
            [LowerDashed, LowerDashed.ToUpperInvariant()],
            await ColumnAsync(connection, """SELECT "Id" FROM "UploadedFiles" ORDER BY "Id" DESC;"""));
    }

    private static async Task InstallAsync(SqliteConnection connection, GrimoireSchemaVersionChainSet chains, int version)
    {
        GrimoireSchemaInstallResult result = await GrimoireSchemaTestInstaller.InstallAsync(
            connection, chains, 1536, CancellationToken.None);

        Assert.Equal(GrimoireSchemaTierHealth.Healthy, result.Core.Health);

        Assert.Equal(version, result.Core.SchemaVersion);
    }

    private static async Task InsertUploadedFileAsync(SqliteConnection connection, string id)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO "UploadedFiles" ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt")
            VALUES ($id, 'f.jsonl', 5, 'batch', 'application/jsonl', $createdAt);
            """;

        _ = command.Parameters.AddWithValue("$id", id);

        _ = command.Parameters.AddWithValue("$createdAt", UtcInstantText.Format(DateTimeOffset.UtcNow));

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertBatchAsync(
        SqliteConnection connection,
        string id,
        string input,
        string? output,
        string? error)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO "Batches" ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "OutputFileId", "ErrorFileId")
            VALUES ($id, $input, '/v1/chat/completions', 'completed', $createdAt, $output, $error);
            """;

        _ = command.Parameters.AddWithValue("$id", id);

        _ = command.Parameters.AddWithValue("$input", input);

        _ = command.Parameters.AddWithValue("$createdAt", UtcInstantText.Format(DateTimeOffset.UtcNow));

        _ = command.Parameters.AddWithValue("$output", (object?)output ?? DBNull.Value);

        _ = command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ColumnAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        List<string> values = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
        }

        return [.. values];
    }

    private static async Task<IReadOnlyDictionary<string, string>> DefinitionsAsync(bool evolve)
    {
        using EvolutionScratchDatabase file = EvolutionScratchDatabase.Create();

        await using SqliteConnection connection = await file.OpenAsync(CancellationToken.None);

        if (evolve)
        {
            await InstallAsync(connection, CoreSchemaVersionFourteenFixture.ChainSet(), 14);
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
