using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Raw-SQL operation ledger stored inside the encrypted Grimoire. All ownership and lifecycle
/// mutations are compare-and-swap updates; no process-local lock is required for correctness.
/// </summary>
internal sealed class LongRunningOperationStore(
    ArcanumDbContext db,
    IGrimoireOrdinaryConnectionFactory connections,
    ICovenantConnectionDrain? covenantDrain = null) : ILongRunningOperationStore, IDisposable
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

    private readonly IGrimoireOrdinaryConnectionFactory _connections =
        connections ?? throw new ArgumentNullException(nameof(connections));

    private IDisposable? _covenantDrainEnrolment =
        covenantDrain is not null && db.Database.GetDbConnection() is SqliteConnection sqlite
            ? covenantDrain.Register(sqlite)
            : null;

    /// <summary>
    /// Releases the scoped ledger handle from the process-wide Covenant drain.
    /// </summary>
    /// <remarks>
    /// The connection is enrolled before its first open. Destructive maintenance can therefore
    /// close it before a file replacement, and the next store call reopens the same EF connection
    /// object against the installed candidate instead of continuing on the detached old file.
    /// </remarks>
    public void Dispose() =>
        Interlocked.Exchange(ref _covenantDrainEnrolment, null)?.Dispose();

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

    public Task<LongRunningOperationRequestIdentityResult> ResolveOrCreateAsync(
        LongRunningOperationCreateRequest request,
        LongRunningOperationRequestIdentity identity,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(identity);

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicSummary);

        if (identity.RequestedOperationId == Guid.Empty)
        {

            throw new ArgumentException(
                "A requested operation identity cannot be empty; an empty name would collide with every other empty one.",
                nameof(identity));

        }

        if (!identity.ApplyRequestDigest.IsValid || !identity.EffectDigest.IsValid)
        {

            throw new ArgumentException(
                "A requested operation identity carries a complete apply-request digest and effect digest.",
                nameof(identity));

        }

        if (!LongRunningOperationPolicyCatalog.IsRegistered(request.Kind, request.RecoveryPolicy))
        {

            throw new ArgumentException(
                $"Operation kind '{request.Kind}' is not registered with recovery policy '{request.RecoveryPolicy}'.",
                nameof(request));

        }

        string kind = Bound(request.Kind, MaxKindLength);

        string summary = Bound(request.PublicSummary, MaxSummaryLength);

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // BEGIN IMMEDIATE: the lookup and the two inserts have to be one write transaction,
                // or two concurrent applies both read "no such identity" and both create an
                // operation, which is exactly the double start this identity exists to prevent.
                await using DbTransaction transaction = await connection
                    .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false);

                Guid? existingOperationId = null;

                byte[]? existingDigest = null;

                await using (DbCommand lookup = connection.CreateCommand())
                {

                    lookup.Transaction = transaction;

                    lookup.CommandText =
                        """
                        SELECT "OperationId", "ApplyRequestDigest"
                        FROM long_running_operation_request_identities
                        WHERE "RequestedOperationId" = @requested
                        LIMIT 1
                        """;

                    Add(lookup, "@requested", Format(identity.RequestedOperationId));

                    await using DbDataReader reader = await lookup
                        .ExecuteReaderAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {

                        existingOperationId = ParseGuid(reader.GetString(0));

                        existingDigest = (byte[])reader.GetValue(1);

                    }

                }

                if (existingOperationId is { } resolved)
                {

                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    // A different digest under the same name means the caller changed what it was
                    // asking for. Returning the existing operation would invite it to be treated as
                    // the one that was requested.
                    if (existingDigest is not { Length: CovenantLimits.DigestBytes }
                        || !CryptographicOperations.FixedTimeEquals(
                            identity.ApplyRequestDigest.Bytes,
                            existingDigest))
                    {

                        return new LongRunningOperationRequestIdentityResult(
                            LongRunningOperationRequestIdentityOutcome.DigestConflict,
                            Operation: null);

                    }

                    LongRunningOperation? replayed = await GetAsync(resolved, cancellationToken).ConfigureAwait(false);

                    return new LongRunningOperationRequestIdentityResult(
                        LongRunningOperationRequestIdentityOutcome.Replayed,
                        replayed);

                }

                Guid id = Guid.NewGuid();

                await using (DbCommand insert = connection.CreateCommand())
                {

                    insert.Transaction = transaction;

                    insert.CommandText =
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

                    Add(insert, "@id", Format(id));
                    Add(insert, "@kind", kind);
                    Add(insert, "@state", (int)LongRunningOperationState.Pending);
                    Add(insert, "@policy", (int)request.RecoveryPolicy);
                    Add(insert, "@root", FormatNullable(request.RootOperationId));
                    Add(insert, "@parent", FormatNullable(request.ParentOperationId));
                    Add(insert, "@session", FormatNullable(request.SessionId));
                    Add(insert, "@run", FormatNullable(request.RunId));
                    Add(insert, "@inference", FormatNullable(request.InferenceRunId));
                    Add(insert, "@reservation", FormatNullable(request.BudgetReservationId));
                    Add(insert, "@claim", FormatNullable(request.IdempotencyClaimId));
                    Add(insert, "@created", Format(request.CreatedAt));
                    Add(insert, "@summary", summary);

                    _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await using (DbCommand link = connection.CreateCommand())
                {

                    link.Transaction = transaction;

                    link.CommandText =
                        """
                        INSERT INTO long_running_operation_request_identities
                            ("OperationId", "RequestedOperationId", "ApplyRequestDigest", "EffectDigest", "CreatedAtUtc")
                        VALUES
                            (@operation, @requested, @apply, @effect, @created)
                        """;

                    Add(link, "@operation", Format(id));
                    Add(link, "@requested", Format(identity.RequestedOperationId));
                    Add(link, "@apply", identity.ApplyRequestDigest.Bytes);
                    Add(link, "@effect", identity.EffectDigest.Bytes);
                    Add(link, "@created", Format(request.CreatedAt));

                    _ = await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return new LongRunningOperationRequestIdentityResult(
                    LongRunningOperationRequestIdentityOutcome.Created,
                    new LongRunningOperation(
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
                        Revision: 0));

            },
            cancellationToken);

    }

    public Task<LongRunningOperation?> TryStartSingleFlightAsync(
        LongRunningOperationCreateRequest request,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicSummary);

        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);

        if (!LongRunningOperationPolicyCatalog.IsRegistered(
                request.Kind,
                request.RecoveryPolicy))
        {

            throw new ArgumentException(
                $"Operation kind '{request.Kind}' is not registered with recovery policy '{request.RecoveryPolicy}'.",
                nameof(request));

        }

        if (leaseExpiresAt <= utcNow)
        {

            throw new ArgumentOutOfRangeException(
                nameof(leaseExpiresAt),
                "Lease expiry must be in the future.");

        }

        Guid id = Guid.NewGuid();

        string kind = Bound(request.Kind, MaxKindLength);

        string summary = Bound(request.PublicSummary, MaxSummaryLength);

        string owner = Bound(ownerId, MaxOwnerLength);

        return SqliteBusyRetry.ExecuteAsync<LongRunningOperation?>(
            async () =>
            {

                DbConnection connection = await OpenConnectionAsync(
                    cancellationToken).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                // A single SQLite write statement is an implicit transaction, so competing

                // connections cannot both pass the active-operation predicate and insert.

                command.CommandText =
                    """
                    INSERT INTO "LongRunningOperations"
                        ("Id", "Kind", "State", "RecoveryPolicy", "RootOperationId", "ParentOperationId",
                         "SessionId", "RunId", "InferenceRunId", "BudgetReservationId", "IdempotencyClaimId",
                         "CreatedAt", "StartedAt", "HeartbeatAt", "CompletedAt", "LeaseOwner", "LeaseExpiresAt",
                         "AttemptCount", "CheckpointVersion", "CheckpointPayload", "CheckpointReference",
                         "PublicSummary", "TerminalErrorCode", "Revision")
                    SELECT
                        @id, @kind, @running, @policy, @root, @parent,
                        @session, @run, @inference, @reservation, @claim,
                        @created, @now, @now, NULL, @owner, @lease,
                        1, 0, NULL, NULL, @summary, NULL, 1
                    WHERE NOT EXISTS (
                        SELECT 1
                        FROM "LongRunningOperations"
                        WHERE (
                              "Kind" = @kind
                              OR (@retentionKind = 1 AND "Kind" LIKE 'data-retention-%'))
                          AND "State" IN (
                              @pending,
                              @running,
                              @waiting,
                              @cancelling,
                              @reconciliation))
                    """;

                Add(command, "@id", Format(id));

                Add(command, "@kind", kind);

                Add(
                    command,
                    "@retentionKind",
                    kind.StartsWith("data-retention-", StringComparison.Ordinal) ? 1 : 0);

                Add(command, "@running", (int)LongRunningOperationState.Running);

                Add(command, "@pending", (int)LongRunningOperationState.Pending);

                Add(command, "@waiting", (int)LongRunningOperationState.Waiting);

                Add(command, "@cancelling", (int)LongRunningOperationState.Cancelling);

                Add(
                    command,
                    "@reconciliation",
                    (int)LongRunningOperationState.ReconciliationRequired);

                Add(command, "@policy", (int)request.RecoveryPolicy);

                Add(command, "@root", FormatNullable(request.RootOperationId));

                Add(command, "@parent", FormatNullable(request.ParentOperationId));

                Add(command, "@session", FormatNullable(request.SessionId));

                Add(command, "@run", FormatNullable(request.RunId));

                Add(command, "@inference", FormatNullable(request.InferenceRunId));

                Add(command, "@reservation", FormatNullable(request.BudgetReservationId));

                Add(command, "@claim", FormatNullable(request.IdempotencyClaimId));

                Add(command, "@created", Format(request.CreatedAt));

                Add(command, "@now", Format(utcNow));

                Add(command, "@owner", owner);

                Add(command, "@lease", Format(leaseExpiresAt));

                Add(command, "@summary", summary);

                int inserted = await command
                    .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                return inserted == 0
                    ? null
                    : new LongRunningOperation(
                        Id: id,
                        Kind: kind,
                        State: LongRunningOperationState.Running,
                        RecoveryPolicy: request.RecoveryPolicy,
                        RootOperationId: request.RootOperationId,
                        ParentOperationId: request.ParentOperationId,
                        SessionId: request.SessionId,
                        RunId: request.RunId,
                        InferenceRunId: request.InferenceRunId,
                        BudgetReservationId: request.BudgetReservationId,
                        IdempotencyClaimId: request.IdempotencyClaimId,
                        CreatedAt: request.CreatedAt,
                        StartedAt: utcNow,
                        HeartbeatAt: utcNow,
                        CompletedAt: null,
                        LeaseOwner: owner,
                        LeaseExpiresAt: leaseExpiresAt,
                        AttemptCount: 1,
                        CheckpointVersion: 0,
                        CheckpointPayload: null,
                        CheckpointReference: null,
                        PublicSummary: summary,
                        TerminalErrorCode: null,
                        Revision: 1);

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

    /// <summary>
    /// Reads back the normalized identity row an operation was created under.
    /// </summary>
    /// <remarks>
    /// Keyed by the durable server operation id rather than by the caller's requested name, because
    /// that is the direction recovery and the erasure planners need: they already hold the operation
    /// and are asking what, if anything, was promised about it. A server-generated operation has no
    /// row and returns null.
    /// </remarks>
    public Task<LongRunningOperationRequestIdentity?> FindRequestIdentityAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    """
                    SELECT "RequestedOperationId", "ApplyRequestDigest", "EffectDigest"
                    FROM long_running_operation_request_identities
                    WHERE "OperationId" = @id
                    LIMIT 1
                    """;
                Add(cmd, "@id", Format(operationId));
                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    ? new LongRunningOperationRequestIdentity(
                        ParseGuid(reader.GetString(0)),
                        new CovenantDigest((byte[])reader.GetValue(1)),
                        new CovenantDigest((byte[])reader.GetValue(2)))
                    : null;
            },
            cancellationToken);

    public Task<LongRunningOperationRequestIdentityMatch?> FindByRequestedOperationIdAsync(
        Guid requestedOperationId,
        CancellationToken cancellationToken = default)
    {

        if (requestedOperationId == Guid.Empty)
        {

            throw new ArgumentException(
                "A requested operation identity cannot be empty.",
                nameof(requestedOperationId));

        }

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using DbCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    $"""
                    SELECT {SelectColumns},
                           identity."RequestedOperationId",
                           identity."ApplyRequestDigest",
                           identity."EffectDigest"
                    FROM "LongRunningOperations" AS operation
                    INNER JOIN long_running_operation_request_identities AS identity
                        ON identity."OperationId" = operation."Id"
                    WHERE identity."RequestedOperationId" = @requested
                    LIMIT 1
                    """;
                Add(cmd, "@requested", Format(requestedOperationId));
                await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    return null;

                }

                return new LongRunningOperationRequestIdentityMatch(
                    Read(reader),
                    new LongRunningOperationRequestIdentity(
                        ParseGuid(reader.GetString(24)),
                        new CovenantDigest((byte[])reader.GetValue(25)),
                        new CovenantDigest((byte[])reader.GetValue(26))));
            },
            cancellationToken);

    }

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

    /// <summary>
    /// Every row no live worker can still drive forward. Beyond expired Running/Waiting leases this
    /// includes two states that would otherwise have no exit at all:
    /// <list type="bullet">
    /// <item>
    /// <c>Cancelling</c> — only the kinds that poll the flag settle their own row, and only while
    /// their lease is alive. Once the lease lapses nobody observes it, and the state is accepted
    /// nowhere else, so an unobserved cancellation would wedge its kind forever.
    /// </item>
    /// <item>
    /// <c>Pending</c> with a prior attempt — what <c>arcanum operation retry</c> produces. A row at
    /// attempt zero is excluded deliberately: its creator is about to lease it in the very next
    /// statement, and reconciling it would race the caller that just made it.
    /// </item>
    /// </list>
    /// </summary>
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
                    WHERE (
                        "State" IN (@running, @waiting, @cancelling)
                        OR ("State" = @pending AND "AttemptCount" > 0)
                        OR (
                            "State" = @attention
                            AND "Kind" IN (@retentionPrune, @retentionMutation, @retentionFactory)
                            AND "TerminalErrorCode" = @retentionRecoveryError)
                        OR (
                            "State" = @attention
                            AND "Kind" IN (@retentionMutation, @retentionFactory)
                            AND "TerminalErrorCode" = @covenantMaintenanceError))
                      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= @now)
                    ORDER BY COALESCE("LeaseExpiresAt", "CreatedAt"), "Id"
                    LIMIT @limit
                    """;
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
                Add(cmd, "@cancelling", (int)LongRunningOperationState.Cancelling);
                Add(cmd, "@pending", (int)LongRunningOperationState.Pending);
                Add(cmd, "@attention", (int)LongRunningOperationState.ReconciliationRequired);
                Add(cmd, "@retentionPrune", LongRunningOperationKinds.DataRetentionPrune);
                Add(cmd, "@retentionMutation", LongRunningOperationKinds.DataRetentionMutation);
                Add(cmd, "@retentionFactory", LongRunningOperationKinds.DataRetentionFactoryReset);
                Add(cmd, "@retentionRecoveryError", ErrorCodes.Data.ReconciliationFailed);
                Add(cmd, "@covenantMaintenanceError", ErrorCodes.Covenant.MaintenanceFailed);
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
                      AND (
                          "State" IN (@pending, @running, @waiting, @cancelling)
                          OR (
                              "State" = @attention
                              AND "Kind" IN (@retentionPrune, @retentionMutation, @retentionFactory)
                              AND "TerminalErrorCode" = @retentionRecoveryError)
                          OR (
                              "State" = @attention
                              AND "Kind" IN (@retentionMutation, @retentionFactory)
                              AND "TerminalErrorCode" = @covenantMaintenanceError)
                          OR (
                              "State" = @attention
                              AND "Kind" = @a2aInbound
                              AND "TerminalErrorCode" = @a2aParked))
                      AND ("LeaseOwner" IS NULL OR "LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= @now)
                    """;
                Add(cmd, "@running", (int)LongRunningOperationState.Running);
                Add(cmd, "@pending", (int)LongRunningOperationState.Pending);
                Add(cmd, "@waiting", (int)LongRunningOperationState.Waiting);
                Add(cmd, "@cancelling", (int)LongRunningOperationState.Cancelling);
                Add(cmd, "@attention", (int)LongRunningOperationState.ReconciliationRequired);
                Add(cmd, "@retentionPrune", LongRunningOperationKinds.DataRetentionPrune);
                Add(cmd, "@retentionMutation", LongRunningOperationKinds.DataRetentionMutation);
                Add(cmd, "@retentionFactory", LongRunningOperationKinds.DataRetentionFactoryReset);
                Add(cmd, "@retentionRecoveryError", ErrorCodes.Data.ReconciliationFailed);
                Add(cmd, "@covenantMaintenanceError", ErrorCodes.Covenant.MaintenanceFailed);

                // A Sending parked awaiting a peer's answer is flagged rather than closed, and the answer
                // may arrive processes later — so that flagged row has to stay claimable, or the record
                // that makes the continuation work becomes unusable the moment it is recorded (#68).
                Add(cmd, "@a2aInbound", LongRunningOperationKinds.A2AInboundSending);
                Add(cmd, "@a2aParked", LongRunningOperationRecoveryOutcomes.A2AInboundParkedAwaitingAnswer);
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

    /// <summary>
    /// Renews a held lease. Always from a connection this store owns, never the caller's scoped one.
    /// </summary>
    /// <remarks>
    /// Every heartbeat in the repo fires from a background timer while the owner's handler is still
    /// working. Running the UPDATE on the workload's own scoped <c>ArcanumDbContext</c> connection
    /// put two writers on one <see cref="SqliteConnection"/>, whose live-command list is not
    /// synchronized, and folded the renewal into whatever transaction the workload had open — so a
    /// rolled-back unit of work silently took the lease renewal with it and the reconciler could
    /// steal an operation whose owner was still running. <see cref="RenewLeaseAsync"/> already
    /// opened its own connection for this reason; the isolated-heartbeat ordinary lease preserves
    /// that independent, unpooled lifetime while making admission and physical draining explicit.
    /// </remarks>
    public Task<bool> HeartbeatAsync(
        Guid operationId,
        string ownerId,
        DateTimeOffset utcNow,
        DateTimeOffset leaseExpiresAt,
        CancellationToken cancellationToken = default) =>
        RenewLeaseAsync(operationId, ownerId, utcNow, leaseExpiresAt, cancellationToken);

    public Task<bool> RenewLeaseAsync(
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

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {

                Result<IGrimoireOrdinaryConnectionLease> acquired = await _connections
                    .OpenFreshAsync(
                        GrimoireOrdinaryFreshConnectionKind.IsolatedHeartbeat,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (acquired.IsFailure)
                {

                    throw new GrimoireMaintenanceUnavailableException();

                }

                await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

                SqliteConnection connection = lease.Connection;

                await using SqliteCommand command = connection.CreateCommand();

                command.CommandText =
                    """
                    UPDATE "LongRunningOperations"
                    SET "HeartbeatAt" = @now, "LeaseExpiresAt" = @lease, "Revision" = "Revision" + 1
                    WHERE "Id" = @id AND "LeaseOwner" = @owner
                      AND "State" IN (@running, @waiting, @cancelling)
                      AND "LeaseExpiresAt" > @now
                    """;

                Add(command, "@id", Format(operationId));

                Add(command, "@owner", Bound(ownerId, MaxOwnerLength));

                Add(command, "@now", Format(utcNow));

                Add(command, "@lease", Format(leaseExpiresAt));

                Add(command, "@running", (int)LongRunningOperationState.Running);

                Add(command, "@waiting", (int)LongRunningOperationState.Waiting);

                Add(command, "@cancelling", (int)LongRunningOperationState.Cancelling);

                return await command.ExecuteNonQueryAsync(
                    cancellationToken).ConfigureAwait(false) == 1;

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
        if (checkpointVersion <= 0
            || checkpointVersion < expectedCheckpointVersion)
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

    /// <summary>
    /// Returns a row to <c>Pending</c> for another attempt. Failed, Abandoned and
    /// ReconciliationRequired have all released their lease, so no live worker can be reset out from
    /// under. Cancelling is admitted only once its lease has lapsed, which is exactly the case no
    /// owner will ever settle — the operator must be able to back out of a cancellation nobody
    /// observed, without being able to yank a cancellation still in progress.
    /// </summary>
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
              AND (
                  "State" IN (@failed, @abandoned, @attention)
                  OR (
                      "State" = @cancelling
                      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= @now)))
            """,
            cmd =>
            {
                Add(cmd, "@id", Format(operationId));
                Add(cmd, "@revision", expectedRevision);
                Add(cmd, "@pending", (int)LongRunningOperationState.Pending);
                Add(cmd, "@failed", (int)LongRunningOperationState.Failed);
                Add(cmd, "@abandoned", (int)LongRunningOperationState.Abandoned);
                Add(cmd, "@attention", (int)LongRunningOperationState.ReconciliationRequired);
                Add(cmd, "@cancelling", (int)LongRunningOperationState.Cancelling);
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
