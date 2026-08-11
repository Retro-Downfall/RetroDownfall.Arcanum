using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <see cref="UnseenServantWatermark"/> rows, reusing the scoped
/// <see cref="ArcanumDbContext"/>'s connection. The <c>UnseenServantWatermarks</c> table is not
/// part of the compiled EF model (created by the embedded <c>InitialCreate.sql</c> schema baseline
/// under <c>Data/Schema/Tables/</c>), so all access goes through <see cref="DbCommand"/> rather than LINQ.
/// </summary>
internal sealed class UnseenServantWatermarkStore(ArcanumDbContext db) : IUnseenServantWatermarkStore
{

    public async Task<UnseenServantWatermark?> GetAsync(string jobKey, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "JobKey", "LastRunAt", "EffectiveIntervalMinutes"
                    FROM "UnseenServantWatermarks"
                    WHERE "JobKey" = @jobKey
                    LIMIT 1
                    """;

                AddParameter(cmd, "@jobKey", jobKey);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadWatermark(reader);
            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task SaveAsync(
        string jobKey,
        DateTimeOffset lastRunAt,
        int effectiveIntervalMinutes,
        CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "UnseenServantWatermarks" ("JobKey", "LastRunAt", "EffectiveIntervalMinutes")
                    VALUES (@jobKey, @lastRunAt, @interval)
                    ON CONFLICT("JobKey") DO UPDATE SET
                        "LastRunAt" = @lastRunAt,
                        "EffectiveIntervalMinutes" = @interval
                    """;

                AddParameter(cmd, "@jobKey", jobKey);

                AddParameter(cmd, "@lastRunAt", lastRunAt.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@interval", effectiveIntervalMinutes);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    }

    public async Task<IReadOnlyList<UnseenServantWatermark>> GetAllAsync(CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "JobKey", "LastRunAt", "EffectiveIntervalMinutes"
                    FROM "UnseenServantWatermarks"
                    ORDER BY "JobKey"
                    """;

                List<UnseenServantWatermark> watermarks = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    watermarks.Add(ReadWatermark(reader));
                }

                return (IReadOnlyList<UnseenServantWatermark>)watermarks;
            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task DeleteAsync(string jobKey, CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "UnseenServantWatermarks"
                    WHERE "JobKey" = @jobKey
                    """;

                AddParameter(cmd, "@jobKey", jobKey);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

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

    private static UnseenServantWatermark ReadWatermark(DbDataReader reader)
    {

        string jobKey = reader.GetString(0);

        DateTimeOffset lastRunAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);

        int effectiveIntervalMinutes = reader.GetInt32(2);

        return new UnseenServantWatermark(jobKey, lastRunAt, effectiveIntervalMinutes);

    }

}
