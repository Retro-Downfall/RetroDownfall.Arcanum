using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class BlobEncryptionMetadataStoreTests
{
    [SkippableTheory]
    [InlineData("identity")]
    [InlineData("length-text")]
    [InlineData("length-real")]
    [InlineData("length-negative")]
    [InlineData("version-overflow")]
    [InlineData("version-real")]
    [InlineData("version-negative")]
    [InlineData("hash-blob")]
    [InlineData("key-blob")]
    [InlineData("purpose-blob")]
    public async Task Corrupt_uploaded_metadata_does_not_hide_a_healthy_neighbor(string corruption)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string healthyId = Guid.NewGuid().ToString("D");

        await InsertUploadedMetadataAsync(healthyId, 5L, 0L, "custom-user-purpose", null, null, 12);

        await InsertUploadedMetadataAsync(
            corruption == "identity" ? "PRIVATE-invalid-identity\n/private/secret" : Guid.NewGuid().ToString("D"),
            corruption switch { "length-text" => "PRIVATE-size", "length-real" => 1.5d, "length-negative" => -1L, _ => 5L },
            corruption switch { "version-overflow" => (long)int.MaxValue + 1, "version-real" => 1.5d, "version-negative" => -1L, _ => 0L },
            corruption == "purpose-blob" ? new byte[] { 1, 2 } : "batch",
            corruption == "hash-blob" ? new byte[] { 1, 2 } : null,
            corruption == "key-blob" ? new byte[] { 1, 2 } : null,
            -17);

        TestCapturingLogger<BlobEncryptionMetadataStore> logger = new();

        BlobEncryptionMetadataStore store = new(_db!, logger);

        BlobEncryptionInventory inventory = await store.ListAsync();

        BlobEncryptionCandidate healthy = Assert.Single(inventory.Candidates);

        Assert.Equal(healthyId, healthy.RecordId);

        Assert.Equal(5, healthy.ExpectedPlaintextLength);

        Assert.Equal(EncryptedBlobPurpose.UploadedFile, healthy.Purpose);

        Assert.Equal(12, healthy.CatalogRowId);

        BlobEncryptionInvalidRow invalid = Assert.Single(inventory.InvalidRows);

        Assert.Equal(-17, invalid.CatalogRowId);

        Assert.Equal(BlobEncryptionRecordKind.UploadedFile, invalid.Kind);

        Assert.Equal(corruption.Split('-')[0] switch
        {
            "identity" => BlobEncryptionMetadataIssue.InvalidIdentity,
            "length" => BlobEncryptionMetadataIssue.InvalidLength,
            "version" => BlobEncryptionMetadataIssue.InvalidVersion,
            "hash" => BlobEncryptionMetadataIssue.InvalidHash,
            "key" => BlobEncryptionMetadataIssue.InvalidKey,
            "purpose" => BlobEncryptionMetadataIssue.InvalidPurpose,
            _ => throw new InvalidOperationException(),
        }, invalid.Issue);

        Assert.Equal(corruption.StartsWith("length-", StringComparison.Ordinal) ? null : (long?)5, invalid.KnownPlaintextLength);

        TestLogEntry entry = Assert.Single(logger.Entries);

        Assert.Null(entry.Exception);

        Assert.DoesNotContain("PRIVATE", entry.Message, StringComparison.Ordinal);

        Assert.Contains("-17", entry.Message, StringComparison.Ordinal);

        Assert.Equal(new[] { "RecordKind", "CatalogRowId", "MetadataIssue", "{OriginalFormat}" }, entry.Properties!.Select(static property => property.Key));
    }

    [SkippableFact]
    public async Task Escaped_attachment_metadata_does_not_hide_a_healthy_upload()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string healthyId = Guid.NewGuid().ToString("D");

        await InsertUploadedMetadataAsync(healthyId, 5L, 0L, "batch", null, null, 12);

        await ExecuteAsync("""
            INSERT INTO SessionAttachments
                (rowid, Id, State, LogicalKey, OriginalFileName, Version, RelativePath,
                 ContentSha256, MimeType, ByteLength, Kind, CreatedAt)
            VALUES (-17, 'AAAA0000-0000-4000-8000-000000000000', 'Pending', 'x', 'x', 1,
                '../../PRIVATE-path', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'text/plain', 5, 'Text', '2026-10-27T00:00:00.0000000Z');
            """);

        BlobEncryptionMetadataStore store = new(_db!, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlobEncryptionMetadataStore>.Instance);

        BlobEncryptionCandidate healthy = Assert.Single((await store.ListAsync()).Candidates);

        Assert.Equal(healthyId, healthy.RecordId);
    }

    [SkippableFact]
    public async Task Vanished_metadata_is_a_per_record_failure_with_a_safe_locator()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string id = Guid.NewGuid().ToString("D");

        await InsertUploadedMetadataAsync(id, 5L, 0L, "batch", null, null, -17);

        BlobEncryptionMetadataStore store = new(_db!, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlobEncryptionMetadataStore>.Instance);

        BlobEncryptionCandidate candidate = Assert.Single((await store.ListAsync()).Candidates);

        await ExecuteAsync("DELETE FROM UploadedFiles WHERE rowid = -17;");

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateEncryptionMetadataAsync(
            candidate,
            new EncryptedBlobDescriptor(EncryptedBlobFormat.CurrentVersion, EncryptedBlobAlgorithm.Aes256Gcm,
                4096, KeyId, 5, 64, EncryptedBlobPurpose.UploadedFile, ReadOnlyMemory<byte>.Empty),
            new string('a', 64)));

        Assert.Contains("UploadedFile", failure.Message, StringComparison.Ordinal);

        Assert.Contains("-17", failure.Message, StringComparison.Ordinal);
    }

    private async Task InsertUploadedMetadataAsync(string id, object bytes, object version, object purpose,
        object? hash, object? key, long rowId)
    {
        await _db!.Database.OpenConnectionAsync();

        await using DbCommand command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = """
            INSERT INTO UploadedFiles
                (rowid, Id, Filename, Bytes, Purpose, MimeType, CreatedAt, EncryptionVersion, EncryptionKeyId, PlaintextSha256)
            VALUES (@rowid, @id, 'PRIVATE-filename', @bytes, @purpose, 'application/octet-stream',
                '2026-10-27T00:00:00.0000000Z', @version, @key, @hash);
            """;

        foreach ((string name, object? value) in new (string, object?)[]
                 { ("@rowid", rowId), ("@id", id), ("@bytes", bytes), ("@purpose", purpose), ("@version", version), ("@key", key), ("@hash", hash) })
        {
            DbParameter parameter = command.CreateParameter();

            parameter.ParameterName = name;

            parameter.Value = value ?? DBNull.Value;

            command.Parameters.Add(parameter);
        }

        await command.ExecuteNonQueryAsync();
    }
}
