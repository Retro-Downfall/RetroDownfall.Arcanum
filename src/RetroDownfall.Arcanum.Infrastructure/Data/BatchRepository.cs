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
/// <c>Data/Schema/Tables/</c>), so all access goes through <see cref="DbCommand"/> rather than LINQ
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
                    INSERT INTO "Batches" (
                        "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt",
                        "OutputFileId", "ErrorFileId", "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount")
                    SELECT @id, @inputFileId, @endpoint, @status, @createdAt, @completedAt,
                           @outputFileId, @errorFileId, @totalRequestCount, @completedRequestCount, @failedRequestCount
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

                AddParameter(cmd, "@id", record.Id.ToString("N"));

                AddParameter(cmd, "@inputFileId", record.InputFileId.ToString());

                AddParameter(cmd, "@inputFileKey", record.InputFileId.ToString("N"));

                AddParameter(cmd, "@endpoint", record.Endpoint);

                AddParameter(cmd, "@status", record.Status);

                AddParameter(cmd, "@createdAt", record.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@completedAt", (object?)record.CompletedAt?.ToString("o", CultureInfo.InvariantCulture) ?? DBNull.Value);

                AddParameter(cmd, "@outputFileId", (object?)record.OutputFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@outputFileKey", (object?)record.OutputFileId?.ToString("N") ?? DBNull.Value);

                AddParameter(cmd, "@errorFileId", (object?)record.ErrorFileId?.ToString() ?? DBNull.Value);

                AddParameter(cmd, "@errorFileKey", (object?)record.ErrorFileId?.ToString("N") ?? DBNull.Value);

                AddParameter(cmd, "@totalRequestCount", record.TotalRequestCount);

                AddParameter(cmd, "@completedRequestCount", record.CompletedRequestCount);

                AddParameter(cmd, "@failedRequestCount", record.FailedRequestCount);

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
                    SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                           "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                    FROM "Batches"
                    WHERE "Id" = @id
                    LIMIT 1
                    """;

                AddParameter(cmd, "@id", id.ToString("N"));

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
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                               "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                        FROM "Batches"
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        """;

                }
                else
                {

                    cmd.CommandText =
                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                               "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                        FROM "Batches"
                        WHERE "Status" = @status
                        ORDER BY "CreatedAt" DESC, "Id" DESC
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

    public async Task<BatchListPage> ListPageAsync(

        string? status,

        BatchListPosition? after,

        int pageSize,

        CancellationToken cancellationToken = default)

    {

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        int fetchSize = checked(pageSize + 1);

        return await SqliteBusyRetry.ExecuteAsync(

            async () =>

            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                bool filtered = !string.IsNullOrWhiteSpace(status);

                if (!filtered && after is null)

                {

                    command.CommandText =

                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                               "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                        FROM "Batches"
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        LIMIT @fetchSize
                        """;

                }

                else if (filtered && after is null)

                {

                    command.CommandText =

                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                               "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                        FROM "Batches"
                        WHERE "Status" = @status
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        LIMIT @fetchSize
                        """;

                }

                else if (!filtered)

                {

                    command.CommandText =

                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                               "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                        FROM "Batches"
                        WHERE "CreatedAt" < @afterCreatedAt
                           OR ("CreatedAt" = @afterCreatedAt AND "Id" < @afterId)
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        LIMIT @fetchSize
                        """;

                }

                else

                {

                    command.CommandText =

                        """
                        SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                               "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                        FROM "Batches"
                        WHERE "Status" = @status
                          AND (
                              "CreatedAt" < @afterCreatedAt
                              OR ("CreatedAt" = @afterCreatedAt AND "Id" < @afterId)
                          )
                        ORDER BY "CreatedAt" DESC, "Id" DESC
                        LIMIT @fetchSize
                        """;

                }

                AddParameter(command, "@fetchSize", fetchSize);

                if (filtered)

                {

                    AddParameter(command, "@status", status!);

                }

                if (after is not null)

                {

                    AddParameter(

                        command,

                        "@afterCreatedAt",

                        after.CreatedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));

                    AddParameter(command, "@afterId", after.Id.ToString("N"));

                }

                List<BatchRecord> records = new(fetchSize);

                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))

                {

                    records.Add(ReadRecord(reader));

                }

                bool hasMore = records.Count > pageSize;

                if (hasMore)

                {

                    records.RemoveAt(records.Count - 1);

                }

                return new BatchListPage(records, hasMore);

            },

            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<BatchRecord>> ListPendingPageAsync(

        int pageSize,

        CancellationToken cancellationToken = default)
    {

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                           "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
                    FROM "Batches"
                    WHERE "Status" = @validating
                    ORDER BY "CreatedAt" ASC, "Id" ASC
                    LIMIT @pageSize
                    """;

                AddParameter(cmd, "@validating", BatchStatuses.Validating);

                AddParameter(cmd, "@pageSize", pageSize);

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
                    SELECT "Id", "InputFileId", "Endpoint", "Status", "CreatedAt", "CompletedAt", "OutputFileId", "ErrorFileId",
                           "TotalRequestCount", "CompletedRequestCount", "FailedRequestCount"
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

                AddParameter(cmd, "@id", id.ToString("N"));

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

                AddParameter(cmd, "@id", id.ToString("N"));

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

    public async Task<IReadOnlyList<BatchLineCheckpoint>> ListLineCheckpointsAsync(

        Guid batchId,

        long firstLine,

        long lastLine,

        CancellationToken cancellationToken = default)

    {

        ArgumentOutOfRangeException.ThrowIfLessThan(firstLine, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(lastLine, firstLine);

        return await SqliteBusyRetry.ExecuteAsync(

            async () =>

            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =

                    """
                    SELECT "BatchId", "LineNumber", "CustomId", "State", "OutputKind", "Outcome", "JsonLine", "DispatchedAt", "CompletedAt"
                    FROM "BatchLineCheckpoints"
                    WHERE "BatchId" = @batchId
                      AND "LineNumber" >= @firstLine
                      AND "LineNumber" <= @lastLine
                    ORDER BY "LineNumber" ASC
                    """;

                AddParameter(command, "@batchId", batchId.ToString("N"));

                AddParameter(command, "@firstLine", firstLine);

                AddParameter(command, "@lastLine", lastLine);

                return await ReadLineCheckpointsAsync(command, cancellationToken).ConfigureAwait(false);

            },

            cancellationToken).ConfigureAwait(false);

    }

    public async Task<IReadOnlyList<BatchLineCheckpoint>> ListLineCheckpointsAsync(

        Guid batchId,

        BatchLineCheckpointState state,

        long afterLine,

        int pageSize,

        CancellationToken cancellationToken = default)

    {

        ArgumentOutOfRangeException.ThrowIfNegative(afterLine);

        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return await SqliteBusyRetry.ExecuteAsync(

            async () =>

            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =

                    """
                    SELECT "BatchId", "LineNumber", "CustomId", "State", "OutputKind", "Outcome", "JsonLine", "DispatchedAt", "CompletedAt"
                    FROM "BatchLineCheckpoints"
                    WHERE "BatchId" = @batchId
                      AND "State" = @state
                      AND "LineNumber" > @afterLine
                    ORDER BY "LineNumber" ASC
                    LIMIT @pageSize
                    """;

                AddParameter(command, "@batchId", batchId.ToString("N"));

                AddParameter(command, "@state", (int)state);

                AddParameter(command, "@afterLine", afterLine);

                AddParameter(command, "@pageSize", pageSize);

                return await ReadLineCheckpointsAsync(command, cancellationToken).ConfigureAwait(false);

            },

            cancellationToken).ConfigureAwait(false);

    }

    public async Task<bool> TryBeginLineAsync(

        Guid batchId,

        long lineNumber,

        string customId,

        CancellationToken cancellationToken = default)

    {

        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);

        ArgumentException.ThrowIfNullOrWhiteSpace(customId);

        return await SqliteBusyRetry.ExecuteAsync(

            async () =>

            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =

                    """
                    INSERT INTO "BatchLineCheckpoints"
                        ("BatchId", "LineNumber", "CustomId", "State", "OutputKind", "Outcome", "JsonLine", "DispatchedAt", "CompletedAt")
                    SELECT @batchId, @lineNumber, @customId, @dispatched, NULL, NULL, NULL, @dispatchedAt, NULL
                    FROM "Batches"
                    WHERE "Id" = @batchId
                      AND "Status" = @inProgress
                      AND NOT EXISTS (
                          SELECT 1
                          FROM "BatchLineCheckpoints"
                          WHERE "BatchId" = @batchId
                            AND "LineNumber" = @lineNumber
                      )
                    """;

                AddParameter(command, "@batchId", batchId.ToString("N"));

                AddParameter(command, "@lineNumber", lineNumber);

                AddParameter(command, "@customId", customId);

                AddParameter(command, "@dispatched", (int)BatchLineCheckpointState.Dispatched);

                AddParameter(command, "@dispatchedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(command, "@inProgress", BatchStatuses.InProgress);

                return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

            },

            cancellationToken).ConfigureAwait(false);

    }

    public Task CompleteLineAsync(

        Guid batchId,

        long lineNumber,

        BatchLineOutputKind outputKind,

        BatchRequestOutcome outcome,

        string jsonLine,

        CancellationToken cancellationToken = default)

    {

        ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber, 1);

        ArgumentNullException.ThrowIfNull(jsonLine);

        return SqliteBusyRetry.ExecuteAsync(

            async () =>

            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =

                    """
                    UPDATE "BatchLineCheckpoints"
                    SET "State" = @completed,
                        "OutputKind" = @outputKind,
                        "Outcome" = @outcome,
                        "JsonLine" = @jsonLine,
                        "CompletedAt" = @completedAt
                    WHERE "BatchId" = @batchId
                      AND "LineNumber" = @lineNumber
                      AND "State" = @dispatched
                    """;

                AddParameter(command, "@completed", (int)BatchLineCheckpointState.Completed);

                AddParameter(command, "@outputKind", (int)outputKind);

                AddParameter(command, "@outcome", (int)outcome);

                AddParameter(command, "@jsonLine", jsonLine);

                AddParameter(command, "@completedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(command, "@batchId", batchId.ToString("N"));

                AddParameter(command, "@lineNumber", lineNumber);

                AddParameter(command, "@dispatched", (int)BatchLineCheckpointState.Dispatched);

                int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                if (rows == 1)

                {

                    return;

                }

                IReadOnlyList<BatchLineCheckpoint> existing = await ReadLineCheckpointAsync(

                    connection,

                    batchId,

                    lineNumber,

                    cancellationToken).ConfigureAwait(false);

                if (existing.Count == 1

                    && existing[0].State == BatchLineCheckpointState.Completed

                    && existing[0].OutputKind == outputKind

                    && existing[0].Outcome == outcome

                    && string.Equals(existing[0].JsonLine, jsonLine, StringComparison.Ordinal))

                {

                    return;

                }

                throw new InvalidOperationException(

                    $"Batch line checkpoint '{batchId:D}/{lineNumber}' was missing or already completed with different content.");

            },

            cancellationToken);

    }

    public Task DeleteLineCheckpointsAsync(

        Guid batchId,

        CancellationToken cancellationToken = default)

    {

        return SqliteBusyRetry.ExecuteAsync(

            async () =>

            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                command.CommandText =

                    """
                    DELETE FROM "BatchLineCheckpoints"
                    WHERE "BatchId" = @batchId
                    """;

                AddParameter(command, "@batchId", batchId.ToString("N"));

                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            },

            cancellationToken);

    }

    private static async Task<IReadOnlyList<BatchLineCheckpoint>> ReadLineCheckpointAsync(

        DbConnection connection,

        Guid batchId,

        long lineNumber,

        CancellationToken cancellationToken)

    {

        await using DbCommand command = connection.CreateCommand();

        command.CommandText =

            """
            SELECT "BatchId", "LineNumber", "CustomId", "State", "OutputKind", "Outcome", "JsonLine", "DispatchedAt", "CompletedAt"
            FROM "BatchLineCheckpoints"
            WHERE "BatchId" = @batchId
              AND "LineNumber" = @lineNumber
            LIMIT 1
            """;

        AddParameter(command, "@batchId", batchId.ToString("N"));

        AddParameter(command, "@lineNumber", lineNumber);

        return await ReadLineCheckpointsAsync(command, cancellationToken).ConfigureAwait(false);

    }

    private static async Task<IReadOnlyList<BatchLineCheckpoint>> ReadLineCheckpointsAsync(

        DbCommand command,

        CancellationToken cancellationToken)

    {

        List<BatchLineCheckpoint> checkpoints = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))

        {

            checkpoints.Add(ReadLineCheckpoint(reader));

        }

        return checkpoints;

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

        AddParameter(command, "@id", id.ToString("N"));

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

        long totalRequestCount = reader.GetInt64(8);

        long completedRequestCount = reader.GetInt64(9);

        long failedRequestCount = reader.GetInt64(10);

        return new BatchRecord(

            id,

            inputFileId,

            endpoint,

            status,

            createdAt,

            completedAt,

            outputFileId,

            errorFileId,

            totalRequestCount,

            completedRequestCount,

            failedRequestCount);

    }

    private static BatchLineCheckpoint ReadLineCheckpoint(DbDataReader reader)

    {

        Guid batchId = Guid.Parse(reader.GetString(0));

        long lineNumber = reader.GetInt64(1);

        string customId = reader.GetString(2);

        BatchLineCheckpointState state = (BatchLineCheckpointState)reader.GetInt32(3);

        BatchLineOutputKind? outputKind = reader.IsDBNull(4)

            ? null

            : (BatchLineOutputKind)reader.GetInt32(4);

        BatchRequestOutcome? outcome = reader.IsDBNull(5)

            ? null

            : (BatchRequestOutcome)reader.GetInt32(5);

        string? jsonLine = reader.IsDBNull(6) ? null : reader.GetString(6);

        DateTimeOffset dispatchedAt = DateTimeOffset.Parse(

            reader.GetString(7),

            CultureInfo.InvariantCulture);

        DateTimeOffset? completedAt = reader.IsDBNull(8)

            ? null

            : DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);

        return new BatchLineCheckpoint(

            batchId,

            lineNumber,

            customId,

            state,

            outputKind,

            outcome,

            jsonLine,

            dispatchedAt,

            completedAt);

    }

}
