using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <c>BudgetAlerts</c> rows, reusing the scoped <see cref="ArcanumDbContext"/>'s
/// connection. The <c>BudgetAlerts</c> table is created by the embedded
/// <c>20260706040100_AddBudgetAlerts.sql</c> migration and is deliberately not part of the compiled EF
/// model. A unique index on (<c>Threshold</c>, <c>date(AlertedAt)</c>) prevents duplicate per-day alerts;
/// <see cref="RecordAlertAsync"/> swallows the resulting <c>SQLITE_CONSTRAINT</c> and returns <see langword="false"/>.
/// </summary>
internal sealed class BudgetAlertRepository(ArcanumDbContext db, ILogger<BudgetAlertRepository> logger) : IBudgetAlertRepository
{

    public async Task<bool> RecordAlertAsync(
        int threshold,
        decimal spendUsd,
        decimal dailyLimitUsd,
        CancellationToken cancellationToken = default)
    {

        try
        {

            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.Transaction = transaction;

                cmd.CommandText =
                    """
                    INSERT INTO "BudgetAlerts" ("Id", "Threshold", "AlertedAt", "SpendUsd", "DailyLimitUsd")
                    VALUES (@id, @threshold, @alertedAt, @spendUsd, @dailyLimitUsd)
                    """;

                AddParameter(cmd, "@id", Guid.NewGuid().ToString("N"));
                AddParameter(cmd, "@threshold", threshold);
                AddParameter(cmd, "@alertedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@spendUsd", spendUsd);
                AddParameter(cmd, "@dailyLimitUsd", dailyLimitUsd);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return true;

            }, cancellationToken).ConfigureAwait(false);

        }
        catch (DbException ex) when (IsConstraintViolation(ex))
        {

            logger.LogWarning(
                "Budget alert for threshold {Threshold} already recorded today; skipping duplicate.",
                threshold);

            return false;

        }

    }

    public async Task<bool> HasAlertedTodayAsync(int threshold, CancellationToken cancellationToken = default)
    {

        string todayText = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return await SqliteBusyRetry.ExecuteAsync(async () =>
        {

            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbCommand cmd = connection.CreateCommand();

            cmd.CommandText =
                """
                SELECT COUNT(*)
                FROM "BudgetAlerts"
                WHERE "Threshold" = @threshold AND date("AlertedAt") = date(@today)
                """;

            AddParameter(cmd, "@threshold", threshold);
            AddParameter(cmd, "@today", todayText);

            object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            return result is long count && count > 0;

        }, cancellationToken).ConfigureAwait(false);

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

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private static bool IsConstraintViolation(DbException ex)
    {

        if (ex is SqliteException sqliteException)
        {

            return sqliteException.SqliteErrorCode == 19; // SQLITE_CONSTRAINT

        }

        return false;

    }

}
