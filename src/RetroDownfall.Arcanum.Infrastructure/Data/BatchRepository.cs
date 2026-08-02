using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <see cref="BatchRecord"/> rows, reusing the scoped
/// <see cref="ArcanumDbContext"/>'s connection. The <c>Batches</c> table is not part of the compiled
/// EF model (created by the embedded <c>InitialCreate.sql</c> schema baseline under
/// <c>Data/SqlMigrations/</c>), so all access goes through <see cref="DbCommand"/> rather than LINQ
/// — mirrors <see cref="UploadedFileRepository"/>.
/// </summary>
internal sealed class BatchRepository(ArcanumDbContext db) : IBatchRepository
{

    public Task CreateAsync(BatchRecord record, CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "Batches" ("Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId")
                    SELECT @id, @inputFileId, @endpoint, @status, @createdAt, @completedAt, @outputFileId, @errorFileId
                    WHERE EXISTS (
                        SELECT 1
                        FROM "UploadedFiles"
                        WHERE lower(replace("Id", '-', '')) = @inputFileKey
                    )
                      AND (
                          @outputFileId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM "UploadedFiles"
                              WHERE lower(replace("Id", '-', '')) = @outputFileKey
                          )
                      )
                      AND (
                          @errorFileId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM "UploadedFiles"
                              WHERE lower(replace("Id", '-', '')) = @errorFileKey
                          )
                      )
                    """;

                AddParameter(cmd, "@id", record.Id.ToString());

                AddParameter(cmd, "@inputFileId", record.InputFileId.ToString());

                AddParameter(cmd, "@inputFileKey", record.InputFileId.ToString("N"));

                AddParameter(cmd, "@endpoint", record.Endpoint);

                AddParameter(cmd, "@status", record.Status);

                AddParameter(cmd, "@createdAt", record.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@completedAt", (object?)record.CompletedAt?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);

                AddParameter(cmd, "@outputFileId", (object?)record.OutputFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@outputFileKey", (object?)record.OutputFileId?.ToString("N") ?? DBNull.Value);

                AddParameter(cmd, "@errorFileId", (object?)record.ErrorFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@errorFileKey", (object?)record.ErrorFileId?.ToString("N") ?? DBNull.Value);

                int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (rows != 1)
                {

                    throw new BatchFileReferenceException(record.Id);

                }

            },
            cancellationToken);

    }

    public async Task<BatchRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId"
                    FROM "Batches"
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

    public async Task<IReadOnlyList<BatchRecord>> ListAsync(string? status, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                if (string.IsNullOrWhiteSpace(status))
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId"
                        FROM "Batches"
                        ORDER BY "CreatedAt" DESC
                        """;

                }
                else
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId"
                        FROM "Batches"
                        WHERE "Status" = @status
                        ORDER BY "CreatedAt" DESC
                        """;

                    AddParameter(cmd, "@status", status);

                }

                List<BatchRecord> records = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(ReadRecord(reader));
                }

                return (IReadOnlyList<BatchRecord>)records;
            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<BatchRecord>> ListActiveAsync(CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId"
                    FROM "Batches"
                    WHERE "Status" = @validating OR "Status" = @inProgress
                    ORDER BY "CreatedAt" ASC
                    """;

                AddParameter(cmd, "@validating", BatchStatuses.Validating);

                AddParameter(cmd, "@inProgress", BatchStatuses.InProgress);

                List<BatchRecord> records = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(ReadRecord(reader));
                }

                return (IReadOnlyList<BatchRecord>)records;
            },
            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<BatchRecord>> ListByStatusAsync(string status, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId"
                    FROM "Batches"
                    WHERE "Status" = @status
                    ORDER BY "CreatedAt" ASC
                    """;

                AddParameter(cmd, "@status", status);

                List<BatchRecord> records = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    records.Add(ReadRecord(reader));
                }

                return (IReadOnlyList<BatchRecord>)records;
            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task UpdateStatusAsync(
        Guid id,
        string status,
        DateTimeOffset? completedAt,
        Guid? outputFileId,
        Guid? errorFileId,
        CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "Batches"
                    SET "Status" = @status,
                        "CompletedAt" = @completedAt,
                        "OutputFileId" = @outputFileId,
                        "ErrorFileId" = @errorFileId
                    WHERE "Id" = @id
                      AND (
                          @outputFileId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM "UploadedFiles"
                              WHERE lower(replace("Id", '-', '')) = @outputFileKey
                          )
                      )
                      AND (
                          @errorFileId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM "UploadedFiles"
                              WHERE lower(replace("Id", '-', '')) = @errorFileKey
                          )
                      )
                    """;

                AddParameter(cmd, "@id", id.ToString());

                AddParameter(cmd, "@status", status);

                AddParameter(cmd, "@completedAt", (object?)completedAt?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);

                AddParameter(cmd, "@outputFileId", (object?)outputFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@outputFileKey", (object?)outputFileId?.ToString("N") ?? DBNull.Value);

                AddParameter(cmd, "@errorFileId", (object?)errorFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@errorFileKey", (object?)errorFileId?.ToString("N") ?? DBNull.Value);

                int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (rows == 0
                    && await BatchExistsAsync(
                            connection,
                            id,
                            cancellationToken)
                        .ConfigureAwait(false))
                {

                    throw new BatchFileReferenceException(id);

                }

            },
            cancellationToken);

    }

    public async Task<bool> TryCompareAndSetStatusAsync(
        Guid id,
        string expectedStatus,
        string newStatus,
        DateTimeOffset? completedAt,
        Guid? outputFileId,
        Guid? errorFileId,
        CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "Batches"
                    SET "Status" = @newStatus,
                        "CompletedAt" = @completedAt,
                        "OutputFileId" = @outputFileId,
                        "ErrorFileId" = @errorFileId
                    WHERE "Id" = @id AND "Status" = @expectedStatus
                      AND (
                          @outputFileId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM "UploadedFiles"
                              WHERE lower(replace("Id", '-', '')) = @outputFileKey
                          )
                      )
                      AND (
                          @errorFileId IS NULL
                          OR EXISTS (
                              SELECT 1
                              FROM "UploadedFiles"
                              WHERE lower(replace("Id", '-', '')) = @errorFileKey
                          )
                      )
                    """;

                AddParameter(cmd, "@id", id.ToString());

                AddParameter(cmd, "@expectedStatus", expectedStatus);

                AddParameter(cmd, "@newStatus", newStatus);

                AddParameter(cmd, "@completedAt", (object?)completedAt?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);

                AddParameter(cmd, "@outputFileId", (object?)outputFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@outputFileKey", (object?)outputFileId?.ToString("N") ?? DBNull.Value);

                AddParameter(cmd, "@errorFileId", (object?)errorFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@errorFileKey", (object?)errorFileId?.ToString("N") ?? DBNull.Value);

                int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return rows == 1;
            },
            cancellationToken).ConfigureAwait(false);

    }

    private static async Task<bool> BatchExistsAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT 1
            FROM "Batches"
            WHERE "Id" = @id
            LIMIT 1
            """;

        AddParameter(command, "@id", id.ToString());

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            is not null;

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

    private static BatchRecord ReadRecord(DbDataReader reader)
    {

        Guid id = Guid.Parse(reader.GetString(0));

        Guid inputFileId = Guid.Parse(reader.GetString(1));

        string endpoint = reader.GetString(2);

        string status = reader.GetString(3);

        DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);

        DateTimeOffset? completedAt = reader.IsDBNull(5)
            ? null
            : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture);

        Guid? outputFileId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6));

        Guid? errorFileId = reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7));

        return new BatchRecord(id, inputFileId, endpoint, status, createdAt, completedAt, outputFileId, errorFileId);

    }

}
