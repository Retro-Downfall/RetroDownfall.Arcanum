using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Durable idempotency claims with claim-key ≠ fingerprint.
/// </summary>
internal sealed class IdempotencyClaimStore(ArcanumDbContext db) : IIdempotencyClaimStore
{

    public Task<IdempotencyClaim?> TryGetAsync(string claimKeyHash, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId", "LeaseExpiresAt", "HeartbeatAt",
                           "RunId", "StatusCode", "ContentType", "ResponseBody", "TerminalStreamComplete", "CreatedAt", "UpdatedAt"
                    FROM "IdempotencyClaims"
                    WHERE "ClaimKeyHash" = @hash
                    LIMIT 1
                    """;

                AddParameter(cmd, "@hash", claimKeyHash);

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadClaim(reader);
            },
            cancellationToken);
    }

    public Task<IdempotencyClaim?> GetByIdAsync(Guid claimId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    SELECT "Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId", "LeaseExpiresAt", "HeartbeatAt",
                           "RunId", "StatusCode", "ContentType", "ResponseBody", "TerminalStreamComplete", "CreatedAt", "UpdatedAt"
                    FROM "IdempotencyClaims"
                    WHERE "Id" = @id
                    LIMIT 1
                    """;

                AddParameter(cmd, "@id", claimId.ToString("N"));

                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                return ReadClaim(reader);
            },
            cancellationToken);
    }

    public async Task<IdempotencyClaimAcquireResult> TryAcquireAsync(
        IdempotencyClaimAcquireRequest request,
        CancellationToken cancellationToken = default)
    {
        IdempotencyClaim? existing = await TryGetAsync(request.ClaimKeyHash, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            if (!string.Equals(existing.FingerprintHash, request.FingerprintHash, StringComparison.Ordinal))
            {
                return new IdempotencyClaimAcquireResult(Conflict: true, Acquired: false, Claim: existing);
            }

            if (existing.State == IdempotencyClaimState.Completed && existing.TerminalStreamComplete)
            {
                return new IdempotencyClaimAcquireResult(Conflict: false, Acquired: false, Claim: existing);
            }

            if (existing.State is IdempotencyClaimState.Failed or IdempotencyClaimState.Abandoned
                || ((existing.State is IdempotencyClaimState.Running or IdempotencyClaimState.Claimed)
                    && existing.LeaseExpiresAt <= request.CreatedAt))
            {
                bool reclaimed = await TryReclaimAsync(existing.Id, request.OwnerId, request.LeaseExpiresAt, cancellationToken)
                    .ConfigureAwait(false);

                IdempotencyClaim? refreshed = await TryGetAsync(request.ClaimKeyHash, cancellationToken)
                    .ConfigureAwait(false);

                if (refreshed is null)
                {
                    return await TryCreateAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (reclaimed)
                {
                    return new IdempotencyClaimAcquireResult(Conflict: false, Acquired: true, Claim: refreshed);
                }

                bool conflict = !string.Equals(
                    refreshed.FingerprintHash,
                    request.FingerprintHash,
                    StringComparison.Ordinal);

                return new IdempotencyClaimAcquireResult(
                    Conflict: conflict,
                    Acquired: false,
                    Claim: refreshed);
            }

            return new IdempotencyClaimAcquireResult(Conflict: false, Acquired: false, Claim: existing);
        }

        return await TryCreateAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IdempotencyClaimAcquireResult> TryCreateAsync(
        IdempotencyClaimAcquireRequest request,
        CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();

        try
        {
            IdempotencyClaim created = await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    await using DbCommand cmd = connection.CreateCommand();

                    cmd.CommandText =
                        """
                        INSERT INTO "IdempotencyClaims"
                            ("Id", "ClaimKeyHash", "FingerprintHash", "State", "OwnerId", "LeaseExpiresAt", "HeartbeatAt",
                             "RunId", "StatusCode", "ContentType", "ResponseBody", "TerminalStreamComplete", "CreatedAt", "UpdatedAt")
                        VALUES
                            (@id, @claim, @fp, @state, @owner, @lease, @heartbeat,
                             NULL, NULL, NULL, NULL, 0, @created, @updated)
                        """;

                    AddParameter(cmd, "@id", id.ToString("N"));
                    AddParameter(cmd, "@claim", request.ClaimKeyHash);
                    AddParameter(cmd, "@fp", request.FingerprintHash);
                    AddParameter(cmd, "@state", (int)IdempotencyClaimState.Running);
                    AddParameter(cmd, "@owner", request.OwnerId);
                    AddParameter(cmd, "@lease", request.LeaseExpiresAt.ToString("o", CultureInfo.InvariantCulture));
                    AddParameter(cmd, "@heartbeat", request.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
                    AddParameter(cmd, "@created", request.CreatedAt.ToString("o", CultureInfo.InvariantCulture));
                    AddParameter(cmd, "@updated", request.CreatedAt.ToString("o", CultureInfo.InvariantCulture));

                    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                    return new IdempotencyClaim(
                        id,
                        request.ClaimKeyHash,
                        request.FingerprintHash,
                        IdempotencyClaimState.Running,
                        request.OwnerId,
                        request.LeaseExpiresAt,
                        request.CreatedAt,
                        RunId: null,
                        StatusCode: null,
                        ContentType: null,
                        ResponseBody: null,
                        TerminalStreamComplete: false,
                        request.CreatedAt,
                        request.CreatedAt);
                },
                cancellationToken).ConfigureAwait(false);

            return new IdempotencyClaimAcquireResult(Conflict: false, Acquired: true, Claim: created);
        }
        catch (DbException)
        {
            // Unique race — re-read.
            IdempotencyClaim? existing =
                await TryGetAsync(request.ClaimKeyHash, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                throw;
            }

            if (!string.Equals(existing.FingerprintHash, request.FingerprintHash, StringComparison.Ordinal))
            {
                return new IdempotencyClaimAcquireResult(Conflict: true, Acquired: false, Claim: existing);
            }

            return new IdempotencyClaimAcquireResult(Conflict: false, Acquired: false, Claim: existing);
        }
    }

    public Task<bool> HeartbeatAsync(
        Guid claimId,
        string ownerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "IdempotencyClaims"
                    SET "LeaseExpiresAt" = @lease, "HeartbeatAt" = @heartbeat, "UpdatedAt" = @heartbeat
                    WHERE "Id" = @id AND "OwnerId" = @owner AND "State" = @running
                    """;

                DateTimeOffset now = DateTimeOffset.UtcNow;
                AddParameter(cmd, "@id", claimId.ToString("N"));
                AddParameter(cmd, "@owner", ownerId);
                AddParameter(cmd, "@lease", leaseExpiresAt.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@heartbeat", now.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@running", (int)IdempotencyClaimState.Running);

                int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return rows > 0;
            },
            cancellationToken);
    }

    public Task CompleteAsync(
        Guid claimId,
        string ownerId,
        int statusCode,
        string? contentType,
        string responseBody,
        bool terminalStreamValid,
        Guid? runId,
        CancellationToken cancellationToken = default)
    {
        if (!terminalStreamValid)
        {
            return MarkAbandonedAsync(claimId, ownerId, cancellationToken);
        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "IdempotencyClaims"
                    SET "State" = @completed,
                        "StatusCode" = @status,
                        "ContentType" = @contentType,
                        "ResponseBody" = @body,
                        "TerminalStreamComplete" = 1,
                        "RunId" = @runId,
                        "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "OwnerId" = @owner AND "State" = @running
                    """;

                AddParameter(cmd, "@id", claimId.ToString("N"));
                AddParameter(cmd, "@owner", ownerId);
                AddParameter(cmd, "@completed", (int)IdempotencyClaimState.Completed);
                AddParameter(cmd, "@status", statusCode);
                AddParameter(cmd, "@contentType", (object?)contentType ?? DBNull.Value);
                AddParameter(cmd, "@body", responseBody);
                AddParameter(cmd, "@runId", runId is Guid r ? r.ToString("N") : DBNull.Value);
                AddParameter(cmd, "@updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@running", (int)IdempotencyClaimState.Running);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task MarkFailedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default) =>
        MarkTerminalAsync(claimId, ownerId, IdempotencyClaimState.Failed, cancellationToken);

    public Task MarkAbandonedAsync(Guid claimId, string ownerId, CancellationToken cancellationToken = default) =>
        MarkTerminalAsync(claimId, ownerId, IdempotencyClaimState.Abandoned, cancellationToken);

    public Task<bool> TryReclaimAsync(
        Guid claimId,
        string newOwnerId,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                DateTimeOffset now = DateTimeOffset.UtcNow;

                cmd.CommandText =
                    """
                    UPDATE "IdempotencyClaims"
                    SET "OwnerId" = @owner, "LeaseExpiresAt" = @lease, "HeartbeatAt" = @now,
                        "State" = @running, "StatusCode" = NULL, "ContentType" = NULL,
                        "ResponseBody" = NULL, "TerminalStreamComplete" = 0, "UpdatedAt" = @now
                    WHERE "Id" = @id
                      AND (
                            ("State" IN (@runningState, @claimed) AND "LeaseExpiresAt" < @now)
                            OR "State" IN (@failed, @abandoned)
                          )
                    """;

                AddParameter(cmd, "@id", claimId.ToString("N"));
                AddParameter(cmd, "@owner", newOwnerId);
                AddParameter(cmd, "@lease", leaseExpiresAt.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@now", now.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@running", (int)IdempotencyClaimState.Running);
                AddParameter(cmd, "@runningState", (int)IdempotencyClaimState.Running);
                AddParameter(cmd, "@claimed", (int)IdempotencyClaimState.Claimed);
                AddParameter(cmd, "@failed", (int)IdempotencyClaimState.Failed);
                AddParameter(cmd, "@abandoned", (int)IdempotencyClaimState.Abandoned);

                int rows = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return rows > 0;
            },
            cancellationToken);
    }

    public Task LinkRunAsync(Guid claimId, Guid runId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "IdempotencyClaims"
                    SET "RunId" = @runId, "UpdatedAt" = @updated
                    WHERE "Id" = @id
                    """;

                AddParameter(cmd, "@id", claimId.ToString("N"));
                AddParameter(cmd, "@runId", runId.ToString("N"));
                AddParameter(cmd, "@updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    DELETE FROM "IdempotencyClaims"
                    WHERE "CreatedAt" < @olderThan
                      AND "State" IN (@completed, @failed, @abandoned)
                    """;

                AddParameter(cmd, "@olderThan", olderThan.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@completed", (int)IdempotencyClaimState.Completed);
                AddParameter(cmd, "@failed", (int)IdempotencyClaimState.Failed);
                AddParameter(cmd, "@abandoned", (int)IdempotencyClaimState.Abandoned);

                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private Task MarkTerminalAsync(
        Guid claimId,
        string ownerId,
        IdempotencyClaimState state,
        CancellationToken cancellationToken)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "IdempotencyClaims"
                    SET "State" = @state, "TerminalStreamComplete" = 0, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "OwnerId" = @owner AND "State" IN (@running, @claimed)
                    """;

                AddParameter(cmd, "@id", claimId.ToString("N"));
                AddParameter(cmd, "@owner", ownerId);
                AddParameter(cmd, "@state", (int)state);
                AddParameter(cmd, "@updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@running", (int)IdempotencyClaimState.Running);
                AddParameter(cmd, "@claimed", (int)IdempotencyClaimState.Claimed);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static IdempotencyClaim ReadClaim(DbDataReader reader)
    {
        string? runIdText = reader.IsDBNull(7) ? null : reader.GetString(7);
        Guid? runId = runIdText is null ? null : Guid.Parse(runIdText);

        return new IdempotencyClaim(
            Id: Guid.Parse(reader.GetString(0)),
            ClaimKeyHash: reader.GetString(1),
            FingerprintHash: reader.GetString(2),
            State: (IdempotencyClaimState)reader.GetInt32(3),
            OwnerId: reader.GetString(4),
            LeaseExpiresAt: DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            HeartbeatAt: DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            RunId: runId,
            StatusCode: reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ContentType: reader.IsDBNull(9) ? null : reader.GetString(9),
            ResponseBody: reader.IsDBNull(10) ? null : reader.GetString(10),
            TerminalStreamComplete: reader.GetInt32(11) != 0,
            CreatedAt: DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            UpdatedAt: DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture));
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
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

}
