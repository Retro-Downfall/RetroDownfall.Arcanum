using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Telemetry;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <see cref="SanctumBreachRecord"/> rows, reusing the scoped
/// <see cref="ArcanumDbContext"/>'s connection. The <c>SanctumBreaches</c> table is not part of
/// the compiled EF model (see <c>20260703160000_AddSanctumBreaches.sql</c>), so all access goes
/// through <see cref="DbCommand"/> rather than LINQ, mirroring <see cref="UnseenServantWatermarkStore"/>.
/// </summary>
internal sealed class SanctumBreachRepository(ArcanumDbContext db) : ISanctumBreachRepository
{

    public Task RecordAsync(SanctumBreachRecord breach, int maxBreachCount, CancellationToken ct = default)
    {

        int clampedMax = ArcanumSettingClamps.SanctumMaxBreachCount(maxBreachCount);

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                // Transaction and command are created fresh on every invocation of this delegate:
                // if SqliteBusyRetry retries after a SQLITE_BUSY failure, the prior transaction has
                // already been rolled back/disposed by the `await using` blocks below, so the retry
                // starts a brand-new transaction rather than reusing a stale one.
                DbConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

                await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.Transaction = transaction;

                string id = Guid.NewGuid().ToString("N");

                cmd.CommandText =
                    """
                    INSERT INTO "SanctumBreaches"
                        ("Id", "CampaignId", "OccurredAt", "ToolName", "BreachType", "Description", "DetailsJson")
                    VALUES
                        (@id, @campaignId, @occurredAt, @toolName, @breachType, @description, @detailsJson)
                    """;

                string normalizedCampaignId = NormalizeCampaignId(breach.CampaignId);

                AddParameter(cmd, "@id", id);

                AddParameter(cmd, "@campaignId", normalizedCampaignId);

                AddParameter(cmd, "@occurredAt", breach.OccurredAt.ToString("o", CultureInfo.InvariantCulture));

                AddParameter(cmd, "@toolName", breach.ToolName);

                AddParameter(cmd, "@breachType", breach.BreachType);

                AddParameter(cmd, "@description", breach.Description);

                AddParameter(cmd, "@detailsJson", SerializeDetails(breach.Details));

                _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                cmd.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM "SanctumBreaches"
                    WHERE "CampaignId" = @campaignId
                    """;

                cmd.Parameters.Clear();

                AddParameter(cmd, "@campaignId", normalizedCampaignId);

                object? countResult = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

                int count = Convert.ToInt32(countResult, CultureInfo.InvariantCulture);

                if (count > clampedMax)
                {

                    cmd.CommandText =
                        """
                        DELETE FROM "SanctumBreaches"
                        WHERE "Id" IN (
                            SELECT "Id"
                            FROM "SanctumBreaches"
                            WHERE "CampaignId" = @campaignId
                            ORDER BY "OccurredAt" ASC
                            LIMIT @overflow
                        )
                        """;

                    cmd.Parameters.Clear();

                    AddParameter(cmd, "@campaignId", normalizedCampaignId);

                    AddParameter(cmd, "@overflow", count - clampedMax);

                    _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                }

                await transaction.CommitAsync(ct).ConfigureAwait(false);

                ArcanumMetrics.SanctumBreachesTotal.Add(
                    1,
                    new KeyValuePair<string, object?>("breach_type", breach.BreachType));

            },
            ct);

    }

    public async Task<IReadOnlyList<SanctumBreachRecord>> QueryAsync(
        string campaignId,
        int limit,
        DateTimeOffset? before = null,
        string? toolName = null,
        CancellationToken ct = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                StringBuilder sql = new(
                    """
                    SELECT "Id", "CampaignId", "OccurredAt", "ToolName", "BreachType", "Description", "DetailsJson"
                    FROM "SanctumBreaches"
                    WHERE "CampaignId" = @campaignId
                    """);

                AddParameter(cmd, "@campaignId", NormalizeCampaignId(campaignId));

                if (before is not null)
                {

                    sql.Append(" AND \"OccurredAt\" < @before");

                    AddParameter(cmd, "@before", before.Value.ToString("o", CultureInfo.InvariantCulture));

                }

                if (!string.IsNullOrWhiteSpace(toolName))
                {

                    sql.Append(" AND \"ToolName\" = @toolName");

                    AddParameter(cmd, "@toolName", toolName);

                }

                sql.Append(" ORDER BY \"OccurredAt\" DESC LIMIT @limit");

                AddParameter(cmd, "@limit", limit);

                cmd.CommandText = sql.ToString();

                List<SanctumBreachRecord> results = [];

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {

                    results.Add(ReadBreach(reader));

                }

                return (IReadOnlyList<SanctumBreachRecord>)results;

            },
            ct).ConfigureAwait(false);

    }

    public async Task<int> GetCountAsync(string campaignId, CancellationToken ct = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM "SanctumBreaches"
                    WHERE "CampaignId" = @campaignId
                    """;

                AddParameter(cmd, "@campaignId", NormalizeCampaignId(campaignId));

                object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);

                return Convert.ToInt32(result, CultureInfo.InvariantCulture);

            },
            ct).ConfigureAwait(false);

    }

    public async Task<int> DeleteOldestAsync(string campaignId, int count, CancellationToken ct = default)
    {

        if (count <= 0)
        {

            return 0;

        }

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "SanctumBreaches"
                    WHERE "Id" IN (
                        SELECT "Id"
                        FROM "SanctumBreaches"
                        WHERE "CampaignId" = @campaignId
                        ORDER BY "OccurredAt" ASC
                        LIMIT @count
                    )
                    """;

                AddParameter(cmd, "@campaignId", NormalizeCampaignId(campaignId));

                AddParameter(cmd, "@count", count);

                return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            },
            ct).ConfigureAwait(false);

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

    /// <summary>
    /// EF's SQLite provider stores <c>Campaigns.Id</c> (a <see cref="Guid"/> property) as uppercase
    /// "D"-format text (e.g. "CDC8D303-..."), while callers here pass campaign ids in whatever case
    /// <see cref="Guid.ToString()"/> produces (lowercase). SQLite's default TEXT collation is
    /// case-sensitive (BINARY), so the raw-SQL <c>CampaignId</c> column must be normalized to the
    /// same uppercase form or FOREIGN KEY inserts and lookups silently fail to match.
    /// </summary>
    private static string NormalizeCampaignId(string campaignId) =>
        Guid.TryParse(campaignId, out Guid parsed) ? parsed.ToString().ToUpperInvariant() : campaignId;

    private static void AddParameter(DbCommand cmd, string name, object value)
    {

        DbParameter parameter = cmd.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        cmd.Parameters.Add(parameter);

    }

    private static object SerializeDetails(SanctumBreachDetails? details)
    {

        if (details is null)
        {
            return DBNull.Value;
        }

        return JsonSerializer.Serialize(details, TheForgeJsonContext.Default.SanctumBreachDetails);

    }

    private static SanctumBreachDetails? DeserializeDetails(object detailsJson)
    {

        if (detailsJson is not string json || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize(json, TheForgeJsonContext.Default.SanctumBreachDetails);

    }

    private static SanctumBreachRecord ReadBreach(DbDataReader reader)
    {

        string id = reader.GetString(0);

        string campaignId = reader.GetString(1);

        DateTimeOffset occurredAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);

        string toolName = reader.GetString(3);

        string breachType = reader.GetString(4);

        string description = reader.GetString(5);

        object detailsJson = reader.IsDBNull(6) ? DBNull.Value : reader.GetString(6);

        return new SanctumBreachRecord(
            id,
            campaignId,
            occurredAt,
            toolName,
            breachType,
            description,
            DeserializeDetails(detailsJson));

    }

}
