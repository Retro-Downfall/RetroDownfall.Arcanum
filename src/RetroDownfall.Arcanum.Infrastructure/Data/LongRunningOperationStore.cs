using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL operation ledger stored inside the encrypted Grimoire. All ownership and lifecycle
/// mutations are compare-and-swap updates; no process-local lock is required for correctness.
/// </summary>
internal sealed class LongRunningOperationStore(ArcanumDbContext db) : ILongRunningOperationStore
{
    private const int MaxKindLength = 100;
    private const int MaxSummaryLength = 1_024;
    private const int MaxOwnerLength = 200;
    private const int MaxErrorCodeLength = 200;

    private const string SelectColumns =
        """
        "Id", "Kind", "State", "RecoveryPolicy", "RootOperationId", "ParentOperationId",
        "SessionId", "RunId", "InferenceRunId", "BudgetReservationId", "IdempotencyClaimId",
        "CreatedAt", "StartedAt", "HeartbeatAt", "CompletedAt", "LeaseOwner", "LeaseExpiresAt",
        "AttemptCount", "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
        "PublicSummary", "TerminalErrorCode", "Revision"
        """;

    public Task<LongRunningOperation> CreateAsync(
        LongRunningOperationCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicSummary);

        if (!LongRunningOperationPolicyCatalog.IsRegistered(request.Kind, request.RecoveryPolicy))
        {
            throw new ArgumentException(
                $"Operation kind '{request.Kind}' is not registered with recovery policy '{request.RecoveryPolicy}'.",
                nameof(request));
        }

        Guid id = Guid.NewGuid();
        string kind = Bound(request.Kind, MaxKindLength);
        string summary = Bound(request.PublicSummary, MaxSummaryLength);

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    INSERT INTO "LongRunningOperations"
                        ("Id", "Kind", "State", "RecoveryPolicy", "RootOperationId", "ParentOperationId",
                         "SessionId", "RunId", "InferenceRunId", "BudgetReservationId", "IdempotencyClaimId",
                         "CreatedAt", "StartedAt", "HeartbeatAt", "CompletedAt", "LeaseOwner", "LeaseExpiresAt",
                         "AttemptCount", "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
                         "PublicSummary", "TerminalErrorCode", "Revision")
                    VALUES
                        (@id, @kind, @state, @policy, @root, @parent,
                         @session, @run, @inference, @reservation, @claim,
                         @created, NULL, NULL, NULL, NULL, NULL,
                         0, 0, NULL, NULL, @summary, NULL, 0)
                    """;

                Add(cmd, "@id", Format(id));
                Add(cmd, "@kind", kind);
                Add(cmd, "@state", (int)LongRunningOperationState.Pending);
                Add(cmd, "@policy", (int)request.RecoveryPolicy);
                Add(cmd, "@root", FormatNullable(request.RootOperationId));
                Add(cmd, "@parent", FormatNullable(request.ParentOperationId));
                Add(cmd, "@session", FormatNullable(request.SessionId));
                Add(cmd, "@run", FormatNullable(request.RunId));
                Add(cmd, "@inference", FormatNullable(request.InferenceRunId));
                Add(cmd, "@reservation", FormatNullable(request.BudgetReservationId));
                Add(cmd, "@claim", FormatNullable(request.IdempotencyClaimId));
                Add(cmd, "@created", Format(request.CreatedAt));
                Add(cmd, "@summary", summary);

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return new LongRunningOperation(
                    id,
                    kind,
                    LongRunningOperationState.Pending,
                    request.RecoveryPolicy,
                    request.RootOperationId,
                    request.ParentOperationId,
                    request.SessionId,
                    request.RunId,
                    request.InferenceRunId,
                    request.BudgetReservationId,
                    request.IdempotencyClaimId,
                    request.CreatedAt,
                    StartedAt: null,
                    HeartbeatAt: null,
                    CompletedAt: null,
                    LeaseOwner: null,
                    LeaseExpiresAt: null,
                    AttemptCount: 0,
                    CheckpointVersion: 0,
                    CheckpointPayload: null,
                    CheckpointReference: null,
                    summary,
                    TerminalErrorCode: null,
                    Revision: 0);
            },
            cancellationToken);
    }

    public Task<LongRunningOperation?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"""
                    SELECT {SelectColumns}
                    FROM "LongRunningOperations"
                    WHERE "Id" = @id
                    LIMIT 1
                    """;
                Add(cmd, "@id", Format(operationId));
                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? Read(reader)
                    : null;
            },
            cancellationToken);

    public Task<IReadOnlyList<LongRunningOperation>> ListAsync(
        LongRunningOperationQuery query,
        CancellationToken cancellationToken = default)
    {
        int limit = Math.Clamp(query.Limit, 1, 500);
        int offset = Math.Max(0, query.Offset);

        return SqliteBusyRetry.ExecuteAsync<IReadOnlyList<LongRunningOperation>>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"""
                    SELECT {SelectColumns}
                    FROM "LongRunningOperations"
                    WHERE (@kind IS NULL OR "Kind" = @kind)
                      AND (@state IS NULL OR "State" = @state)
                    ORDER BY "CreatedAt" DESC, "Id"
                    LIMIT @limit OFFSET @offset
                    """;
                Add(cmd, "@kind", string.IsNullOrWhiteSpace(query.Kind) ? null : query.Kind);
                Add(cmd, "@state", query.State is null ? null : (int)query.State.Value);
                Add(cmd, "@limit", limit);
                Add(cmd, "@offset", offset);
                return await ReadAllAsync(cmd, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<IReadOnlyList<LongRunningOperation>> FindExpiredAsync(
        DateTimeOffset utcNow,
        int limit,
        CancellationToken cancellationToken = default) =>
        SqliteBusyRetry.ExecuteAsync<IReadOnlyList<LongRunningOperation>>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"""
                    SELECT {SelectColumns}
                    FROM "LongRunningOperations"
                    WHERE "State" IN (@running, @waiting)
                      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= @now)
                    ORDER BY COALESCE("LeaseExpiresAt", "CreatedAt"), "Id"
                    LIMIT @limit
                    """;
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
                Add(cmd, "@now", Format(utcNow));
                Add(cmd, "@limit", Math.Clamp(limit, 1, 1_000));
                return await ReadAllAsync(cmd, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    public async Task<LongRunningOperationLeaseResult> TryAcquireLeaseAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (leaseExpiresAt <= utcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt), "Lease expiry must be in the future.");
        }

        bool acquired = await SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    UPDATE "LongRunningOperations"
                    SET "State" = @running,
                        "StartedAt" = COALESCE("StartedAt", @now),
                        "HeartbeatAt" = @now,
                        "LeaseOwner" = @owner,
                        "LeaseExpiresAt" = @lease,
                        "AttemptCount" = "AttemptCount" + 1,
                        "TerminalErrorCode" = NULL,
                        "Revision" = "Revision" + 1
                    WHERE "Id" = @id
                      AND "State" IN (@pending, @running, @waiting)
                      AND ("LeaseOwner" IS NULL OR "LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= @now)
                    """;
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@pending", (int)LongRunningOperationState.Pending);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
                Add(cmd, "@now", Format(utcNow));
                Add(cmd, "@owner", Bound(ownerId, MaxOwnerLength));
                Add(cmd, "@lease", Format(leaseExpiresAt));
                Add(cmd, "@id", Format(operationId));
                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            },
            cancellationToken).ConfigureAwait(false);

        LongRunningOperation operation = await GetAsync(operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Long-running operation '{operationId}' was not found.");
        return new LongRunningOperationLeaseResult(acquired, operation);
    }

    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        if (leaseExpiresAt <= utcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAt));
        }

        return ExecuteUpdateAsync(
            """
            UPDATE "LongRunningOperations"
            SET "HeartbeatAt" = @now, "LeaseExpiresAt" = @lease, "Revision" = "Revision" + 1
            WHERE "Id" = @id AND "LeaseOwner" = @owner
              AND "State" IN (@running, @waiting, @cancelling)
              AND "LeaseExpiresAt" > @now
            """,
            cmd =>
            {
                Add(cmd, "@id", Format(operationId));
                Add(cmd, "@owner", Bound(ownerId, MaxOwnerLength));
                Add(cmd, "@now", Format(utcNow));
                Add(cmd, "@lease", Format(leaseExpiresAt));
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
                Add(cmd, "@cancelling", (int)LongRunningOperationState.Cancelling);
            },
            cancellationToken);
    }

    public Task<bool> SaveCheckpointAsync(
        Guid operationId,
        string ownerId,
        int expectedCheckpointVersion,
        int checkpointVersion,
        byte[]? checkpointPayload,
        string? checkpointReference,
        string publicSummary,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publicSummary);
        if (checkpointVersion <= expectedCheckpointVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpointVersion));
        }

        return ExecuteUpdateAsync(
            """
            UPDATE "LongRunningOperations"
            SET "CheckpointVersion" = @checkpoint,
                "CheckpointPayload" = @payload,
                "CheckpointReference" = @reference,
                "PublicSummary" = @summary,
                "HeartbeatAt" = @now,
                "Revision" = "Revision" + 1
            WHERE "Id" = @id AND "LeaseOwner" = @owner
              AND "CheckpointVersion" = @expected
              AND "State" IN (@running, @waiting)
            """,
            cmd =>
            {
                Add(cmd, "@id", Format(operationId));
                Add(cmd, "@owner", Bound(ownerId, MaxOwnerLength));
                Add(cmd, "@expected", expectedCheckpointVersion);
                Add(cmd, "@checkpoint", checkpointVersion);
                Add(cmd, "@payload", checkpointPayload);
                Add(cmd, "@reference", checkpointReference);
                Add(cmd, "@summary", Bound(publicSummary, MaxSummaryLength));
                Add(cmd, "@now", Format(utcNow));
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
            },
            cancellationToken);
    }

    public Task<bool> TryTransitionAsync(
        Guid operationId,
        long expectedRevision,
        string? ownerId,
        LongRunningOperationState state,
        DateTimeOffset utcNow,
        string? terminalErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        bool releasesLease = state is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed
            or LongRunningOperationState.Abandoned
            or LongRunningOperationState.ReconciliationRequired;
        bool completed = state is LongRunningOperationState.Completed
            or LongRunningOperationState.Failed
            or LongRunningOperationState.Abandoned;

        return ExecuteUpdateAsync(
            """
            UPDATE "LongRunningOperations"
            SET "State" = @state,
                "CompletedAt" = CASE WHEN @completed = 1 THEN @now ELSE NULL END,
                "LeaseOwner" = CASE WHEN @release = 1 THEN NULL ELSE "LeaseOwner" END,
                "LeaseExpiresAt" = CASE WHEN @release = 1 THEN NULL ELSE "LeaseExpiresAt" END,
                "TerminalErrorCode" = @error,
                "Revision" = "Revision" + 1
            WHERE "Id" = @id AND "Revision" = @revision
              AND (@owner IS NULL OR "LeaseOwner" = @owner)
            """,
            cmd =>
            {
                Add(cmd, "@id", Format(operationId));
                Add(cmd, "@revision", expectedRevision);
                Add(cmd, "@owner", ownerId is null ? null : Bound(ownerId, MaxOwnerLength));
                Add(cmd, "@state", (int)state);
                Add(cmd, "@completed", completed ? 1 : 0);
                Add(cmd, "@release", releasesLease ? 1 : 0);
                Add(cmd, "@now", Format(utcNow));
                Add(cmd, "@error", terminalErrorCode is null ? null : Bound(terminalErrorCode, MaxErrorCodeLength));
            },
            cancellationToken);
    }

    public Task<bool> RequestCancellationAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(
            """
            UPDATE "LongRunningOperations"
            SET "State" = @cancelling, "HeartbeatAt" = @now, "Revision" = "Revision" + 1
            WHERE "Id" = @id AND "Revision" = @revision
              AND "State" IN (@pending, @running, @waiting, @attention)
            """,
            cmd =>
            {
                Add(cmd, "@id", Format(operationId));
                Add(cmd, "@revision", expectedRevision);
                Add(cmd, "@cancelling", (int)LongRunningOperationState.Cancelling);
                Add(cmd, "@pending", (int)LongRunningOperationState.Pending);
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
                Add(cmd, "@attention", (int)LongRunningOperationState.ReconciliationRequired);
                Add(cmd, "@now", Format(utcNow));
            },
            cancellationToken);

    public Task<bool> ResetForRetryAsync(
        Guid operationId,
        long expectedRevision,
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(
            """
            UPDATE "LongRunningOperations"
            SET "State" = @pending,
                "CompletedAt" = NULL,
                "HeartbeatAt" = @now,
                "LeaseOwner" = NULL,
                "LeaseExpiresAt" = NULL,
                "TerminalErrorCode" = NULL,
                "Revision" = "Revision" + 1
            WHERE "Id" = @id AND "Revision" = @revision
              AND "State" IN (@failed, @abandoned, @attention)
            """,
            cmd =>
            {
                Add(cmd, "@id", Format(operationId));
                Add(cmd, "@revision", expectedRevision);
                Add(cmd, "@pending", (int)LongRunningOperationState.Pending);
                Add(cmd, "@failed", (int)LongRunningOperationState.Failed);
                Add(cmd, "@abandoned", (int)LongRunningOperationState.Abandoned);
                Add(cmd, "@attention", (int)LongRunningOperationState.ReconciliationRequired);
                Add(cmd, "@now", Format(utcNow));
            },
            cancellationToken);

    public Task<IReadOnlyList<LongRunningOperationCount>> GetCountsAsync(
        CancellationToken cancellationToken = default) =>
        SqliteBusyRetry.ExecuteAsync<IReadOnlyList<LongRunningOperationCount>>(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT "Kind", "State", COUNT(*)
                    FROM "LongRunningOperations"
                    GROUP BY "Kind", "State"
                    ORDER BY "Kind", "State"
                    """;
                List<LongRunningOperationCount> counts = [];
                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    counts.Add(new LongRunningOperationCount(
                        reader.GetString(0),
                        (LongRunningOperationState)reader.GetInt32(1),
                        reader.GetInt64(2)));
                }

                return counts;
            },
            cancellationToken);

    private Task<bool> ExecuteUpdateAsync(
        string sql,
        Action<DbCommand> bind,
        CancellationToken cancellationToken) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                bind(cmd);
                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            },
            cancellationToken);

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task<IReadOnlyList<LongRunningOperation>> ReadAllAsync(
        DbCommand cmd,
        CancellationToken cancellationToken)
    {
        List<LongRunningOperation> operations = [];
        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            operations.Add(Read(reader));
        }

        return operations;
    }

    private static LongRunningOperation Read(DbDataReader reader) =>
        new(
            ParseGuid(reader.GetString(0)),
            reader.GetString(1),
            (LongRunningOperationState)reader.GetInt32(2),
            (LongRunningOperationRecoveryPolicy)reader.GetInt32(3),
            ReadGuid(reader, 4),
            ReadGuid(reader, 5),
            ReadGuid(reader, 6),
            ReadGuid(reader, 7),
            ReadGuid(reader, 8),
            ReadGuid(reader, 9),
            ReadGuid(reader, 10),
            ParseDate(reader.GetString(11)),
            ReadDate(reader, 12),
            ReadDate(reader, 13),
            ReadDate(reader, 14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            ReadDate(reader, 16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.IsDBNull(19) ? null : (byte[])reader.GetValue(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetString(22),
            reader.GetInt64(23));

    private static Guid? ReadGuid(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseGuid(reader.GetString(ordinal));

    private static DateTimeOffset? ReadDate(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));

    private static Guid ParseGuid(string value) => Guid.ParseExact(value, "N");

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(Guid value) => value.ToString("N");

    private static object? FormatNullable(Guid? value) => value is null ? null : Format(value.Value);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string Bound(string value, int maxLength)
    {
        string trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static void Add(DbCommand cmd, string name, object? value)
    {
        DbParameter parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        _ = cmd.Parameters.Add(parameter);
    }
}
