using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class BlobEncryptionMetadataStore(ArcanumDbContext db, ILogger<BlobEncryptionMetadataStore> logger)
    : IBlobEncryptionMetadataStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task<BlobEncryptionInventory> ListAsync(
        CancellationToken cancellationToken = default) =>
        SqliteBusyRetry.ExecuteAsync<BlobEncryptionInventory>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                List<BlobEncryptionCandidate> candidates = [];
                List<BlobEncryptionInvalidRow> invalid = [];
                await ReadRowsAsync(connection, BlobEncryptionRecordKind.SessionAttachment, candidates, invalid, cancellationToken)
                    .ConfigureAwait(false);
                await ReadRowsAsync(connection, BlobEncryptionRecordKind.UploadedFile, candidates, invalid, cancellationToken)
                    .ConfigureAwait(false);
                return new BlobEncryptionInventory(candidates.ToArray(), invalid.ToArray());
            },
            cancellationToken);

    public async Task UpdateEncryptionMetadataAsync(
        BlobEncryptionCandidate candidate,
        EncryptedBlobDescriptor descriptor,
        string plaintextSha256,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    DbConnection connection = await OpenConnectionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using DbTransaction transaction = await connection
                        .BeginTransactionAsync(cancellationToken)
                        .ConfigureAwait(false);
                    await using DbCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = candidate.Kind switch
                    {
                        BlobEncryptionRecordKind.UploadedFile =>
                            """
                            UPDATE "UploadedFiles"
                            SET "EncryptionVersion" = @version,
                                "EncryptionKeyId" = @keyId,
                                "PlaintextSha256" = @sha256
                            WHERE "Id" = @id
                            """,
                        BlobEncryptionRecordKind.SessionAttachment =>
                            """
                            UPDATE "SessionAttachments"
                            SET "EncryptionVersion" = @version,
                                "EncryptionKeyId" = @keyId
                            WHERE "Id" = @id
                            """,
                        _ => throw new ArgumentOutOfRangeException(nameof(candidate)),
                    };
                    Add(command, "@version", descriptor.Version);
                    Add(command, "@keyId", descriptor.KeyId);
                    Add(command, "@sha256", plaintextSha256);
                    Add(command, "@id", candidate.RecordId);
                    int affected = await command.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (affected != 1)
                    {
                        throw new InvalidDataException(
                            $"Blob encryption metadata {candidate.Kind} row {candidate.CatalogRowId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "<unavailable>"} no longer exists.");
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private async Task ReadRowsAsync(
        DbConnection connection,
        BlobEncryptionRecordKind kind,
        ICollection<BlobEncryptionCandidate> candidates,
        ICollection<BlobEncryptionInvalidRow> invalid,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = kind == BlobEncryptionRecordKind.SessionAttachment
            ? """
              SELECT rowid, "Id", "RelativePath", "ByteLength", "ContentSha256",
                     "EncryptionVersion", "EncryptionKeyId", NULL
              FROM "SessionAttachments" ORDER BY "Id"
              """
            : """
              SELECT rowid, "Id", NULL, "Bytes", "PlaintextSha256",
                     "EncryptionVersion", "EncryptionKeyId", "Purpose"
              FROM "UploadedFiles" ORDER BY "Id"
              """;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long rowId = reader.GetInt64(0);
            long? knownLength = reader.GetValue(3) is long length && length >= 0 ? length : null;
            BlobEncryptionMetadataIssue? issue = DecodeRow(reader, kind, rowId, out BlobEncryptionCandidate? candidate);
            if (issue is { } failure)
            {
                invalid.Add(new BlobEncryptionInvalidRow(kind, rowId, failure, knownLength));
                logger.LogWarning("Blob encryption metadata excluded {RecordKind} row {CatalogRowId}: {MetadataIssue}.",
                    kind, rowId, failure);
            }
            else candidates.Add(candidate!);
        }
    }

    private static BlobEncryptionMetadataIssue? DecodeRow(
        DbDataReader reader,
        BlobEncryptionRecordKind kind,
        long rowId,
        out BlobEncryptionCandidate? candidate)
    {
        candidate = null;
        if (reader.GetValue(1) is not string storedId || !Guid.TryParse(storedId, out Guid id))
            return BlobEncryptionMetadataIssue.InvalidIdentity;
        if (reader.GetValue(3) is not long length || length < 0)
            return BlobEncryptionMetadataIssue.InvalidLength;
        if (reader.GetValue(5) is not long version || version < 0 || version > int.MaxValue)
            return BlobEncryptionMetadataIssue.InvalidVersion;
        object rawHash = reader.GetValue(4);
        if (rawHash is not DBNull && (rawHash is not string text || text.Length != 64 || !text.All(char.IsAsciiHexDigit)))
            return BlobEncryptionMetadataIssue.InvalidHash;
        if (kind == BlobEncryptionRecordKind.SessionAttachment && rawHash is DBNull)
            return BlobEncryptionMetadataIssue.InvalidHash;
        object rawKey = reader.GetValue(6);
        if (rawKey is not DBNull && rawKey is not string)
            return BlobEncryptionMetadataIssue.InvalidKey;
        string path;
        EncryptedBlobPurpose purpose;
        if (kind == BlobEncryptionRecordKind.SessionAttachment)
        {
            if (reader.GetValue(2) is not string relativePath)
                return BlobEncryptionMetadataIssue.InvalidPath;
            try { path = ResolveAttachmentPath(relativePath); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or InvalidDataException)
            { return BlobEncryptionMetadataIssue.InvalidPath; }
            purpose = EncryptedBlobPurpose.SessionAttachment;
        }
        else
        {
            if (reader.GetValue(7) is not string uploadedPurpose)
                return BlobEncryptionMetadataIssue.InvalidPurpose;
            path = UploadedFileStorage.ResolvePath(id);
            purpose = UploadedFileStorage.ResolveEncryptionPurpose(uploadedPurpose);
        }
        // Keep the exact admitted stored spelling for write-back; rowid is only a diagnostic locator.
        candidate = new BlobEncryptionCandidate(kind, storedId, path, purpose, length,
            rawHash as string, (int)version, rawKey as string, rowId);
        return null;
    }

    private static string ResolveAttachmentPath(string relativePath)
    {
        string root = Path.GetFullPath(ArcanumPaths.AttachmentsDirectory);
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Session attachment metadata escapes the attachment root.");
        }

        return candidate;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
