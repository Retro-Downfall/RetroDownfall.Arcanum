using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <see cref="UploadedFileRecord"/> rows, reusing the scoped
/// <see cref="ArcanumDbContext"/>'s connection. The <c>UploadedFiles</c> table is not part of the
/// compiled EF model (created by the embedded <c>InitialCreate.sql</c> schema baseline under
/// <c>Data/SqlMigrations/</c>), so all access goes through <see cref="DbCommand"/> rather than LINQ
/// — mirrors <see cref="UnseenServantWatermarkStore"/>.
/// </summary>
internal sealed class UploadedFileRepository(ArcanumDbContext db) : IUploadedFileRepository
{

    public Task CreateAsync(UploadedFileRecord record, CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "UploadedFiles"
                        ("Id", "Filename", "Bytes", "Purpose", "MimeType", "CreatedAt",
                         "EncryptionVersion", "EncryptionKeyId", "PlaintextSha256")
                    VALUES
                        (@id, @filename, @bytes, @purpose, @mimeType, @createdAt,
                         @encryptionVersion, @encryptionKeyId, @plaintextSha256)
                    """;

                AddParameter(cmd, "@id", record.Id.ToString());

                AddParameter(cmd, "@filename", record.Filename);

                AddParameter(cmd, "@bytes", record.Bytes);

                AddParameter(cmd, "@purpose", record.Purpose);

                AddParameter(cmd, "@mimeType", record.MimeType);

                AddParameter(cmd, "@createdAt", record.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@encryptionVersion", record.EncryptionVersion);

                AddParameter(
                    cmd,
                    "@encryptionKeyId",
                    (object?)record.EncryptionKeyId ?? DBNull.Value);
                AddParameter(
                    cmd,
                    "@plaintextSha256",
                    (object?)record.PlaintextSha256 ?? DBNull.Value);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

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

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {

        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
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
