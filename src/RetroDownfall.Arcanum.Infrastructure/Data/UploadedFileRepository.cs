using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <see cref="UploadedFileRecord"/> rows, reusing the scoped
/// <see cref="ArcanumDbContext"/>'s connection. The <c>UploadedFiles</c> table is not part of the
/// compiled EF model (created by the embedded <c>InitialCreate.sql</c> schema baseline under
/// <c>Data/Schema/Tables/</c>), so all access goes through <see cref="DbCommand"/> rather than LINQ
/// — mirrors <see cref="UnseenServantWatermarkStore"/>.
/// </summary>
internal sealed class UploadedFileRepository : IUploadedFileRepository
{

    private readonly ArcanumDbContext _db;

    private readonly string _filesRoot;

    public UploadedFileRepository(ArcanumDbContext db)
        : this(db, ArcanumPaths.FilesDirectory)
    {

    }

    internal UploadedFileRepository(
        ArcanumDbContext db,
        string filesRoot)
    {

        ArgumentNullException.ThrowIfNull(db);

        ArgumentException.ThrowIfNullOrWhiteSpace(filesRoot);

        _db = db;

        _filesRoot = Path.GetFullPath(filesRoot);

    }

    public Task CreateAsync(UploadedFileRecord record, CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await InsertMetadataAsync(
                        connection,
                        transaction: null,
                        record,
                        cancellationToken)
                    .ConfigureAwait(false);

            },
            cancellationToken);

    }

    public async Task CreateForOwnedFileAsync(
        UploadedFileRecord record,
        CancellationToken cancellationToken = default)
    {

        string path = Path.Combine(_filesRoot, record.Id.ToString("N"));

        if (!IdentityOwnedFileSystemCleanup.TryCapturePath(
                path,
                FileSystemObjectKind.RegularFile,
                out IdentityOwnedFileSystemArtifact expected))
        {

            throw new UploadedFilePublicationException(record.Id);

        }

        try
        {

            await SqliteBusyRetry.ExecuteAsync(
                    () => CreateForOwnedFileOnceAsync(
                        record,
                        expected,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

        }
        catch
        {

            _ = IdentityOwnedFileSystemCleanup.TryDelete(expected);

            throw;

        }

    }

    private async Task CreateForOwnedFileOnceAsync(
        UploadedFileRecord record,
        IdentityOwnedFileSystemArtifact expected,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginImmediateTransactionAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);

        bool transactionCompleted = false;

        try
        {

            if (!IsSameOwnedFile(expected))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                transactionCompleted = true;

                throw new UploadedFilePublicationException(record.Id);

            }

            await InsertMetadataAsync(
                    connection,
                    transaction,
                    record,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            transactionCompleted = true;

        }
        catch
        {

            if (!transactionCompleted)
            {

                _ = await TryRollbackAsync(transaction).ConfigureAwait(false);

            }

            throw;

        }

    }

    public async Task<UploadedFileRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt",
                           "EncryptionVersion", "EncryptionKeyId", "PlaintextSha256"
                    FROM "UploadedFiles"
                    WHERE "Id" = @id
                    LIMIT 1
                    """;

                AddParameter(cmd, "@id", id.ToString());

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadRecord(reader);
            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<UploadedFileRecord>> ListAsync(string? purpose, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                if (string.IsNullOrWhiteSpace(purpose))
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt",
                               "EncryptionVersion", "EncryptionKeyId", "PlaintextSha256"
                        FROM "UploadedFiles"
                        ORDER BY "CreatedAt" DESC
                        """;

                }
                else
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt",
                               "EncryptionVersion", "EncryptionKeyId", "PlaintextSha256"
                        FROM "UploadedFiles"
                        WHERE "Purpose" = @purpose
                        ORDER BY "CreatedAt" DESC
                        """;

                    AddParameter(cmd, "@purpose", purpose);

                }

                List<UploadedFileRecord> records = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(ReadRecord(reader));
                }

                return (IReadOnlyList<UploadedFileRecord>)records;
            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "UploadedFiles"
                    WHERE "Id" = @id
                    """;

                AddParameter(cmd, "@id", id.ToString());

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    }

    public async Task<UploadedFileDeleteStatus> TryDeleteUnreferencedAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            () => TryDeleteUnreferencedOnceAsync(id, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    }

    private async Task<UploadedFileDeleteStatus> TryDeleteUnreferencedOnceAsync(
        Guid id,
        CancellationToken cancellationToken)
    {

        DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using DbTransaction transaction = await BeginImmediateTransactionAsync(
                connection,
                cancellationToken)
            .ConfigureAwait(false);

        IdentityOwnedFileSystemQuarantine quarantine = default;

        bool transactionCompleted = false;

        try
        {

            if (HasRecognizableDeleteQuarantine(id))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                transactionCompleted = true;

                return UploadedFileDeleteStatus.RecoveryRequired;

            }

            UploadedFileDeleteStatus? classification = await ClassifyDeleteAsync(
                    connection,
                    transaction,
                    id,
                    cancellationToken)
                .ConfigureAwait(false);

            if (classification is { } classifiedStatus)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                transactionCompleted = true;

                return classifiedStatus;

            }

            string path = Path.Combine(_filesRoot, id.ToString("N"));

            if (IdentityOwnedFileSystemCleanup.TryCapturePath(
                    path,
                    FileSystemObjectKind.RegularFile,
                    out IdentityOwnedFileSystemArtifact artifact))
            {

                if (!IdentityOwnedFileSystemCleanup.TryQuarantine(
                        artifact,
                        $".arcanum-file-delete-{id:N}-",
                        out quarantine))
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    transactionCompleted = true;

                    return quarantine == default
                        ? UploadedFileDeleteStatus.StorageConflict
                        : UploadedFileDeleteStatus.RecoveryRequired;

                }

                if (quarantine == default)
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    transactionCompleted = true;

                    return UploadedFileDeleteStatus.StorageConflict;

                }

            }
            else if (!IsPathAbsent(path))
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                transactionCompleted = true;

                return UploadedFileDeleteStatus.StorageConflict;

            }

            int deleted = await DeleteUnreferencedMetadataAsync(
                    connection,
                    transaction,
                    id,
                    cancellationToken)
                .ConfigureAwait(false);

            if (deleted != 1)
            {

                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                transactionCompleted = true;

                if (quarantine != default)
                {

                    _ = IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine);

                }

                return UploadedFileDeleteStatus.RecoveryRequired;

            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            transactionCompleted = true;

        }
        catch
        {

            if (!transactionCompleted
                && !await TryRollbackAsync(transaction).ConfigureAwait(false))
            {

                return UploadedFileDeleteStatus.RecoveryRequired;

            }

            if (quarantine != default
                && !IdentityOwnedFileSystemCleanup.TryRestoreQuarantined(quarantine))
            {

                return UploadedFileDeleteStatus.RecoveryRequired;

            }

            throw;

        }

        if (quarantine != default
            && !IdentityOwnedFileSystemCleanup.TryDeleteQuarantined(quarantine))
        {

            return UploadedFileDeleteStatus.RecoveryRequired;

        }

        return UploadedFileDeleteStatus.Deleted;

    }

    private async Task<UploadedFileDeleteStatus?> ClassifyDeleteAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {

        await using DbCommand classify = connection.CreateCommand();

        classify.Transaction = transaction;

        classify.CommandText =
            """
            SELECT CASE
                WHEN EXISTS (
                    SELECT 1
                    FROM "Batches"
                    WHERE lower(replace("InputFileId", '-', '')) = @id
                       OR lower(replace(COALESCE("OutputFileId", ''), '-', '')) = @id
                       OR lower(replace(COALESCE("ErrorFileId", ''), '-', '')) = @id
                ) THEN 1
                ELSE 0
            END
            FROM "UploadedFiles"
            WHERE lower(replace("Id", '-', '')) = @id
            LIMIT 1
            """;

        AddParameter(classify, "@id", id.ToString("N"));

        object? result = await classify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is null)
        {

            return UploadedFileDeleteStatus.NotFound;

        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1
            ? UploadedFileDeleteStatus.ReferencedByBatch
            : null;

    }

    private static async Task<int> DeleteUnreferencedMetadataAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {

        await using DbCommand delete = connection.CreateCommand();

        delete.Transaction = transaction;

        delete.CommandText =
            """
            DELETE FROM "UploadedFiles"
            WHERE lower(replace("Id", '-', '')) = @id
              AND NOT EXISTS (
                  SELECT 1
                  FROM "Batches"
                  WHERE lower(replace("InputFileId", '-', '')) = @id
                     OR lower(replace(COALESCE("OutputFileId", ''), '-', '')) = @id
                     OR lower(replace(COALESCE("ErrorFileId", ''), '-', '')) = @id
              )
            """;

        AddParameter(delete, "@id", id.ToString("N"));

        return await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private bool HasRecognizableDeleteQuarantine(Guid id)
    {

        if (!Directory.Exists(_filesRoot))
        {

            return false;

        }

        try
        {

            return Directory.EnumerateFileSystemEntries(
                    _filesRoot,
                    $".arcanum-file-delete-{id:N}-*",
                    SearchOption.TopDirectoryOnly)
                .Any();

        }
        catch (IOException)
        {

            return true;

        }
        catch (UnauthorizedAccessException)
        {

            return true;

        }

    }

    private static bool IsPathAbsent(string path)
    {

        if (FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out _))
        {

            return false;

        }

        try
        {

            _ = File.GetAttributes(path);

            return false;

        }
        catch (FileNotFoundException)
        {

            return true;

        }
        catch (DirectoryNotFoundException)
        {

            return true;

        }
        catch (IOException)
        {

            return false;

        }
        catch (UnauthorizedAccessException)
        {

            return false;

        }

    }

    private static bool IsSameOwnedFile(
        IdentityOwnedFileSystemArtifact expected) =>
        IdentityOwnedFileSystemCleanup.TryCapturePath(
            expected.Path,
            FileSystemObjectKind.RegularFile,
            out IdentityOwnedFileSystemArtifact current)
        && string.Equals(
            current.Path,
            expected.Path,
            StringComparison.Ordinal)
        && current.Metadata == expected.Metadata;

    private static async Task InsertMetadataAsync(
        DbConnection connection,
        DbTransaction? transaction,
        UploadedFileRecord record,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText =
            """
            INSERT INTO "UploadedFiles"
                ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt",
                 "EncryptionVersion", "EncryptionKeyId", "PlaintextSha256")
            VALUES
                (@id, @filename, @bytes, @purpose, @mimeType, @createdAt,
                 @encryptionVersion, @encryptionKeyId, @plaintextSha256)
            """;

        AddParameter(command, "@id", record.Id.ToString());

        AddParameter(command, "@filename", record.Filename);

        AddParameter(command, "@bytes", record.Bytes);

        AddParameter(command, "@purpose", record.Purpose);

        AddParameter(command, "@mimeType", record.MimeType);

        AddParameter(
            command,
            "@createdAt",
            record.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

        AddParameter(command, "@encryptionVersion", record.EncryptionVersion);

        AddParameter(
            command,
            "@encryptionKeyId",
            (object?)record.EncryptionKeyId ?? DBNull.Value);

        AddParameter(
            command,
            "@plaintextSha256",
            (object?)record.PlaintextSha256 ?? DBNull.Value);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

    }

    private static async Task<DbTransaction> BeginImmediateTransactionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {

        cancellationToken.ThrowIfCancellationRequested();

        if (connection is SqliteConnection sqliteConnection)
        {

            return sqliteConnection.BeginTransaction(deferred: false);

        }

        return await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken)
            .ConfigureAwait(false);

    }

    private static async Task<bool> TryRollbackAsync(DbTransaction transaction)
    {

        try
        {

            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);

            return true;

        }
        catch (DbException)
        {

            return false;

        }
        catch (InvalidOperationException)
        {

            return false;

        }

    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = _db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;

    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private static UploadedFileRecord ReadRecord(DbDataReader reader)
    {

        Guid id = Guid.Parse(reader.GetString(0));

        string filename = reader.GetString(1);

        long bytes = reader.GetInt64(2);

        string purpose = reader.GetString(3);

        string mimeType = reader.GetString(4);

        DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture);

        int encryptionVersion = reader.FieldCount > 6
            ? Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture)
            : 0;
        string? encryptionKeyId = reader.FieldCount > 7 && !reader.IsDBNull(7)
            ? reader.GetString(7)
            : null;
        string? plaintextSha256 = reader.FieldCount > 8 && !reader.IsDBNull(8)
            ? reader.GetString(8)
            : null;

        return new UploadedFileRecord(
            id,
            filename,
            bytes,
            purpose,
            mimeType,
            createdAt,
            encryptionVersion,
            encryptionKeyId,
            plaintextSha256);

    }

}
