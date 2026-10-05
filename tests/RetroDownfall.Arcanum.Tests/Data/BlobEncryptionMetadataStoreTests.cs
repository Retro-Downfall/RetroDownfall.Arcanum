using System.Data;
using System.Data.Common;
using System.Globalization;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// The blob-encryption metadata store reads uploaded-file identities and writes the encryption metadata back
/// to the very rows it read, whichever spelling they hold.
/// </summary>
/// <remarks>
/// Core version 15 settles <c>UploadedFiles.Id</c> on the canonical uppercase dashed spelling, but a row written
/// before it, or one an installation still holds while an earlier sweep drains, is lowercase. The store matches
/// the stored text rather than a rendering of the parsed identity, so a metadata write cannot miss the row it
/// came from and fail the whole migration with "no longer exists".
/// </remarks>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class BlobEncryptionMetadataStoreTests : IAsyncLifetime
{
    private const string KeyId = "test-key";

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public BlobEncryptionMetadataStoreTests(GrimoireFixture fixture) => _fixture = fixture;

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

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateEncryptionMetadataAsync_writes_the_row_it_read_in_either_spelling(bool canonical)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid id = Guid.NewGuid();

        string stored = canonical ? GrimoireEntitySql.Format(id) : id.ToString("D").ToLowerInvariant();

        await ExecuteAsync(
            $"""
            INSERT INTO "UploadedFiles" ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt")
            VALUES ('{stored}', 'f.jsonl', 5, 'batch', 'application/jsonl', '{UtcInstantText.Format(DateTimeOffset.UtcNow)}');
            """);

        BlobEncryptionMetadataStore store = new(_db!);

        BlobEncryptionCandidate candidate = Assert.Single(
            await store.ListAsync(),
            static candidate => candidate.Kind == BlobEncryptionRecordKind.UploadedFile);

        await store.UpdateEncryptionMetadataAsync(
            candidate,
            new EncryptedBlobDescriptor(
                EncryptedBlobFormat.CurrentVersion,
                EncryptedBlobAlgorithm.Aes256Gcm,
                ChunkSize: 4096,
                KeyId,
                PlaintextLength: 5,
                HeaderLength: 64,
                EncryptedBlobPurpose.UploadedFile,
                ReadOnlyMemory<byte>.Empty),
            new string('a', 64));

        Assert.Equal(
            $"{EncryptedBlobFormat.CurrentVersion}|{KeyId}",
            await ScalarAsync(
                $"""SELECT "EncryptionVersion" || '|' || "EncryptionKeyId" FROM "UploadedFiles" WHERE "Id" = '{stored}'"""));
    }

    private async Task ExecuteAsync(string sql)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ScalarAsync(string sql)
    {
        DbConnection connection = _db!.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = sql;

        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
