using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Crash settlement for the single accounting run owned by each batch request page. The batch
/// recovery claim, reservation reconciliation, and run abandonment commit as one write.
/// </summary>
internal sealed class BatchAccountingRecoveryStore(
    ArcanumDbContext db,
    TimeProvider timeProvider) : IBatchAccountingRecoveryStore
{
    public Task<BatchAccountingRecoveryClaimStatus> ClaimRecoveryAsync(
        Guid batchId,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset recoveredAt = timeProvider.GetUtcNow();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                bool claimed = await TryInsertRecoveryClaimAsync(
                    connection,
                    transaction,
                    batchId,
                    cancellationToken).ConfigureAwait(false);

                if (!claimed)
                {
                    bool resumable = await HasRecoveryClaimAsync(
                        connection,
                        transaction,
                        batchId,
                        cancellationToken).ConfigureAwait(false);

                    if (!resumable)
                    {
                        await DeleteRecoveryClaimAsync(
                            connection,
                            transaction,
                            batchId,
                            cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return resumable
                        ? BatchAccountingRecoveryClaimStatus.Resumable
                        : BatchAccountingRecoveryClaimStatus.NotRecoverable;
                }

                IReadOnlyList<string> runningRunIds = await ReadRunningRunIdsAsync(
                    connection,
                    transaction,
                    batchId,
                    cancellationToken).ConfigureAwait(false);

                foreach (string runId in runningRunIds)
                {
                    decimal actualCostUsd = await SumActualCostAsync(
                        connection,
                        transaction,
                        runId,
                        cancellationToken).ConfigureAwait(false);

                    await ReconcileReservationsAsync(
                        connection,
                        transaction,
                        runId,
                        actualCostUsd,
                        recoveredAt,
                        cancellationToken).ConfigureAwait(false);

                    await AbandonRunningRunAsync(
                        connection,
                        transaction,
                        runId,
                        recoveredAt,
                        cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return BatchAccountingRecoveryClaimStatus.Claimed;
            },
            cancellationToken);
    }

    public Task<bool> TryCompleteRecoveryAsync(
        Guid batchId,
        BatchAccountingRecoveryTarget target,
        CancellationToken cancellationToken = default)
    {
        string targetStatus = target switch
        {
            BatchAccountingRecoveryTarget.Requeue => BatchStatuses.Validating,
            BatchAccountingRecoveryTarget.Fail => BatchStatuses.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown batch recovery target."),
        };

        DateTimeOffset recoveredAt = timeProvider.GetUtcNow();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE "Batches"
                    SET "Status" = @target,
                        "CompletedAt" = @completedAt,
                        "OutputFileId" = NULL,
                        "ErrorFileId" = NULL
                    WHERE "Id" = @id
                      AND "Status" = @inProgress
                      AND EXISTS (
                          SELECT 1
                          FROM "BatchAccountingRecoveryClaims"
                          WHERE "BatchId" = @id);
                    """;
                AddParameter(command, "@target", targetStatus);
                AddParameter(
                    command,
                    "@completedAt",
                    target == BatchAccountingRecoveryTarget.Fail
                        ? UtcInstantText.Format(recoveredAt)
                        : DBNull.Value);
                AddParameter(command, "@id", batchId.ToString("N"));
                AddParameter(command, "@inProgress", BatchStatuses.InProgress);

                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return false;
                }

                await using SqliteCommand delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText =
                    "DELETE FROM \"BatchAccountingRecoveryClaims\" WHERE \"BatchId\" = @id;";
                AddParameter(delete, "@id", batchId.ToString("N"));

                if (await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        $"Batch '{batchId:D}' lost its durable accounting recovery claim before completion.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return true;
            },
            cancellationToken);
    }

    private static async Task<bool> TryInsertRecoveryClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO "BatchAccountingRecoveryClaims" ("BatchId")
            SELECT "Id"
            FROM "Batches"
            WHERE "Id" = @id AND "Status" = @inProgress
            ON CONFLICT("BatchId") DO NOTHING;
            """;
        AddParameter(command, "@id", batchId.ToString("N"));
        AddParameter(command, "@inProgress", BatchStatuses.InProgress);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> HasRecoveryClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT 1
            FROM "BatchAccountingRecoveryClaims" AS recovery
            INNER JOIN "Batches" AS batch ON batch."Id" = recovery."BatchId"
            WHERE recovery."BatchId" = @id AND batch."Status" = @inProgress
            LIMIT 1;
            """;
        AddParameter(command, "@id", batchId.ToString("N"));
        AddParameter(command, "@inProgress", BatchStatuses.InProgress);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task DeleteRecoveryClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM \"BatchAccountingRecoveryClaims\" WHERE \"BatchId\" = @id;";
        AddParameter(command, "@id", batchId.ToString("N"));

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ReadRunningRunIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid batchId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT "Id"
            FROM "InferenceRuns"
            WHERE "RequestId" = @requestId
              AND "Surface" = @surface
              AND "Purpose" = @purpose
              AND "Status" = @running
            ORDER BY "StartedAt", "Id";
            """;
        AddParameter(command, "@requestId", $"batch-{batchId:N}");
        AddParameter(command, "@surface", "batch");
        AddParameter(command, "@purpose", "batch");
        AddParameter(command, "@running", (int)InferenceRunStatus.Running);

        List<string> runIds = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            runIds.Add(reader.GetString(0));
        }

        return runIds;
    }

    private static async Task<decimal> SumActualCostAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT "ActualCostUsd"
            FROM "BillableOperations"
            WHERE "RunId" = @runId
            ORDER BY "Id";
            """;
        AddParameter(command, "@runId", runId);

        decimal total = 0m;

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            total = ExactUsdText.CheckedAdd(total, ExactUsdText.Read(reader, 0));
        }

        return total;
    }

    private static async Task ReconcileReservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        decimal actualCostUsd,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE "BudgetReservations"
            SET "ReconciledUsd" = @actual,
                "Status" = @reconciled,
                "UpdatedAt" = @updatedAt
            WHERE "RunId" = @runId AND "Status" = @reserved;
            """;
        _ = ExactUsdText.AddParameter(command, "@actual", actualCostUsd);
        AddParameter(command, "@reconciled", (int)BudgetReservationStatus.Reconciled);
        AddParameter(command, "@updatedAt", UtcInstantText.Format(recoveredAt));
        AddParameter(command, "@runId", runId);
        AddParameter(command, "@reserved", (int)BudgetReservationStatus.Reserved);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AbandonRunningRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE "InferenceRuns"
            SET "Status" = @abandoned, "CompletedAt" = @completedAt
            WHERE "Id" = @runId AND "Status" = @running;
            """;
        AddParameter(command, "@abandoned", (int)InferenceRunStatus.Abandoned);
        AddParameter(command, "@completedAt", UtcInstantText.Format(recoveredAt));
        AddParameter(command, "@runId", runId);
        AddParameter(command, "@running", (int)InferenceRunStatus.Running);

        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static void AddParameter(SqliteCommand command, string name, object value)
    {
        SqliteParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }
}
