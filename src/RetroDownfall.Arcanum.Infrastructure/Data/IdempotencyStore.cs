using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL persistence for <see cref="IdempotencyRecord"/> rows, reusing the scoped
/// <see cref="ArcanumDbContext"/>'s connection. The <c>IdempotencyKeys</c> table is not part of
/// the compiled EF model (created by the embedded <c>InitialCreate.sql</c> schema baseline under
/// <c>Data/Schema/Tables/</c>), so all access goes through <see cref="DbCommand"/> rather than LINQ
/// — mirrors <see cref="UnseenServantWatermarkStore"/>.
/// </summary>
internal sealed class IdempotencyStore(ArcanumDbContext db) : IIdempotencyStore
{

    public async Task<IdempotencyRecord?> TryGetAsync(
        string keyHash,
        DateTimeOffset notOlderThan,
        CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "KeyHash", "StatusCode", "ContentType", "ResponseBody", "CreatedAt"
                    FROM "IdempotencyKeys"
                    WHERE "KeyHash" = @keyHash AND "CreatedAt" >= @notOlderThan
                    LIMIT 1
                    """;

                AddParameter(cmd, "@keyHash", keyHash);

                AddParameter(cmd, "@notOlderThan", notOlderThan.ToString("o", CultureInfo.InvariantCulture));

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadRecord(reader);
            },
            cancellationToken).ConfigureAwait(false);

    }

    public Task SaveAsync(
        string keyHash,
        int statusCode,
        string? contentType,
        string responseBody,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    INSERT INTO "IdempotencyKeys" ("KeyHash", "StatusCode", "ContentType", "ResponseBody", "CreatedAt")
                    VALUES (@keyHash, @statusCode, @contentType, @responseBody, @createdAt)
                    ON CONFLICT("KeyHash") DO UPDATE SET
                        "StatusCode" = @statusCode,
                        "ContentType" = @contentType,
                        "ResponseBody" = @responseBody,
                        "CreatedAt" = @createdAt
                    """;

                AddParameter(cmd, "@keyHash", keyHash);

                AddParameter(cmd, "@statusCode", statusCode);

                AddParameter(cmd, "@contentType", (object?)contentType ?? DBNull.Value);

                AddParameter(cmd, "@responseBody", responseBody);

                AddParameter(cmd, "@createdAt", createdAt.ToString("o", CultureInfo.InvariantCulture));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    }

    public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {

        return await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "IdempotencyKeys"
                    WHERE "CreatedAt" < @olderThan
                    """;

                AddParameter(cmd, "@olderThan", olderThan.ToString("o", CultureInfo.InvariantCulture));

                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

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

    private static IdempotencyRecord ReadRecord(DbDataReader reader)
    {

        string keyHash = reader.GetString(0);

        int statusCode = reader.GetInt32(1);

        string? contentType = reader.IsDBNull(2) ? null : reader.GetString(2);

        string responseBody = reader.GetString(3);

        DateTimeOffset createdAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture);

        return new IdempotencyRecord(keyHash, statusCode, contentType, responseBody, createdAt);

    }

}
