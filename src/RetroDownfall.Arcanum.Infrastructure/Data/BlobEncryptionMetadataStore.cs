using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed class BlobEncryptionMetadataStore(ArcanumDbContext db)
    : IBlobEncryptionMetadataStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task<IReadOnlyList<BlobEncryptionCandidate>> ListAsync(
        CancellationToken cancellationToken = default) =>
        SqliteBusyRetry.ExecuteAsync<IReadOnlyList<BlobEncryptionCandidate>>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken)
                    .ConfigureAwait(false);
                List<BlobEncryptionCandidate> candidates = [];
                await ReadAttachmentsAsync(connection, candidates, cancellationToken)
                    .ConfigureAwait(false);
                await ReadUploadedFilesAsync(connection, candidates, cancellationToken)
                    .ConfigureAwait(false);
                return candidates;
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
                        throw new InvalidOperationException(
                            "Blob encryption metadata row no longer exists.");
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

    private static async Task ReadAttachmentsAsync(
        DbConnection connection,
        ICollection<BlobEncryptionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "Id", "RelativePath", "ByteLength", "ContentSha256",
                   "EncryptionVersion", "EncryptionKeyId"
            FROM "SessionAttachments"
            ORDER BY "Id"
            """;
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string path = ResolveAttachmentPath(reader.GetString(1));
            candidates.Add(new BlobEncryptionCandidate(
                BlobEncryptionRecordKind.SessionAttachment,
                reader.GetString(0),
                path,
                EncryptedBlobPurpose.SessionAttachment,
                reader.GetInt64(2),
                reader.GetString(3),
                Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
    }

    private static async Task ReadUploadedFilesAsync(
        DbConnection connection,
        ICollection<BlobEncryptionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "Id", "Bytes", "Purpose", "EncryptionVersion",
                   "EncryptionKeyId", "PlaintextSha256"
            FROM "UploadedFiles"
            ORDER BY "Id"
            """;
        await using DbDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid id = Guid.Parse(reader.GetString(0));
            string purpose = reader.GetString(2);
            candidates.Add(new BlobEncryptionCandidate(
                BlobEncryptionRecordKind.UploadedFile,
                id.ToString("D"),
                UploadedFileStorage.ResolvePath(id),
                UploadedFileStorage.ResolveEncryptionPurpose(purpose),
                reader.GetInt64(1),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
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
