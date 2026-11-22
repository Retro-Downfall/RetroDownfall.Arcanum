using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Reads measured spend and settles historical reservation records.
/// </summary>
internal sealed partial class BudgetReservationService(ArcanumDbContext db) : IBudgetReservationService
{
    /// <summary>
    /// Sets the UTC day used for reporting in tests.
    /// </summary>
    internal Func<DateTimeOffset>? UtcNowForTesting { get; set; }

    public Task MarkPossiblyDispatchedAsync(Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(async () =>
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbCommand cmd = connection.CreateCommand();

            cmd.CommandText = """
                UPDATE "BudgetReservations" SET "Status" = @uncertain, "UpdatedAt" = @updated
                WHERE "Id" = @id AND "RunId" = @run AND "Status" IN (@reserved, @uncertain)
                """;

            AddParameter(cmd, "@id", reservationId.ToString("N"));

            AddParameter(cmd, "@run", runId.ToString("N"));

            AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);

            AddParameter(cmd, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);

            AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

            if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The owned reservation is no longer available for dispatch.");
            }
        }, cancellationToken);
    }

    public Task ReconcileRecordedUsageAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(async () =>
        {
            SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            decimal? measured = null;

            await using (DbCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;

                // Evaluation owns exactly one physical call per run. A live or unrelated run is
                // never settled by this recovery seam, nor is a partial multi-call ledger.
                read.CommandText = """
                    SELECT bill.ActualCostUsd
                    FROM BillableOperations bill JOIN InferenceRuns run ON run.Id = bill.RunId
                    WHERE run.Id = @run AND run.Surface = 'memory-evaluation'
                      AND run.Purpose = 'counterfactual-arm' AND run.Status <> @running
                      AND bill.Status = @completed
                      AND (SELECT count(*) FROM BillableOperations allBills WHERE allBills.RunId = run.Id) = 1
                    """;

                AddParameter(read, "@run", runId.ToString("N"));

                AddParameter(read, "@running", (int)InferenceRunStatus.Running);

                AddParameter(read, "@completed", (int)BillableOperationStatus.Completed);

                await using DbDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) measured = ExactUsdText.Read(reader, 0);
            }

            if (measured is decimal actual)
            {
                await using DbCommand update = connection.CreateCommand();

                update.Transaction = transaction;

                update.CommandText = """
                    UPDATE BudgetReservations SET Status = @reconciled, ReconciledUsd = @actual, UpdatedAt = @now
                    WHERE RunId = @run AND Status = @uncertain
                    """;

                AddParameter(update, "@run", runId.ToString("N"));

                AddParameter(update, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);

                AddParameter(update, "@reconciled", (int)BudgetReservationStatus.Reconciled);

                ExactUsdText.AddParameter(update, "@actual", actual);

                AddParameter(update, "@now", UtcInstantText.Format(DateTimeOffset.UtcNow));

                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "ReconciledUsd" = @actual, "Status" = @status, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "Status" IN (@reserved, @uncertain)
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                _ = ExactUsdText.AddParameter(cmd, "@actual", Math.Max(0m, actualCostUsd));
                AddParameter(cmd, "@status", (int)BudgetReservationStatus.Reconciled);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);
                AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "Status" = @status, "ReconciledUsd" = @zero, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "Status" = @reserved
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                AddParameter(cmd, "@status", (int)BudgetReservationStatus.Released);
                _ = ExactUsdText.AddParameter(cmd, "@zero", 0m);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default)
    {
        string period = TodayBudgetPeriod();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                return await SumCommittedAsync(connection, transaction: null, period, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default)
    {
        string period = TodayBudgetPeriod();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                return await SumOutstandingAsync(connection, transaction: null, period, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "Status" = @expired, "UpdatedAt" = @updated
                    WHERE "Status" = @reserved AND "ExpiresAt" < @now
                    """;

                AddParameter(cmd, "@expired", (int)BudgetReservationStatus.Expired);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@now", UtcInstantText.Format(utcNow));
                AddParameter(cmd, "@updated", UtcInstantText.Format(utcNow));

                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static string UtcBudgetPeriod(DateTimeOffset utcNow) =>
        utcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The budget period a provider call made now lands its spend in.</summary>
    private string TodayBudgetPeriod() =>
        UtcBudgetPeriod(UtcNowForTesting?.Invoke() ?? DateTimeOffset.UtcNow);

    private static async Task<decimal> SumCommittedAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string budgetPeriod,
        CancellationToken cancellationToken)
    {
        // Only measured operations completed on this UTC day contribute to this view.
        DateTimeOffset start = DateTimeOffset.Parse(
            budgetPeriod + "T00:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        string dayStart = UtcInstantText.Format(start);

        string dayEnd = UtcInstantText.Format(start.AddDays(1));

        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT "ActualCostUsd"
            FROM "BillableOperations"
            WHERE "CompletedAt" >= @dayStart AND "CompletedAt" < @dayEnd
            """;

        AddParameter(cmd, "@dayStart", dayStart);
        AddParameter(cmd, "@dayEnd", dayEnd);

        decimal total = 0m;

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            total = ExactUsdText.CheckedAdd(total, ExactUsdText.Read(reader, 0));
        }

        return total;
    }

    private static async Task<decimal> SumOutstandingAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string budgetPeriod,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT "ReservedUsd", "ReconciledUsd"
            FROM "BudgetReservations"
            WHERE ("BudgetPeriod" = @period AND "Status" = @reserved) OR "Status" = @uncertain
            """;

        AddParameter(cmd, "@period", budgetPeriod);
        AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
        AddParameter(cmd, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);

        decimal total = 0m;

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Measured partial usage can exceed the conservative estimate after a provider overrun.
            // It remains real spend and uncertain work remains uncertain, but a hold never becomes credit.
            decimal outstanding = Math.Max(0m, checked(ExactUsdText.Read(reader, 0) - ExactUsdText.Read(reader, 1)));

            total = ExactUsdText.CheckedAdd(total, outstanding);
        }

        return total;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return (SqliteConnection)connection;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// What a recheck judges: the reservation's own budget period, and today when that is another
    /// day (null on the reservation's own day). Today's figure already holds this reservation.
    /// </summary>
}
