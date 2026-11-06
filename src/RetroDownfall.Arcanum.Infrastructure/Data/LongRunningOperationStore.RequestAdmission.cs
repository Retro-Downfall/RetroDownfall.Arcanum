using System.Data.Common;

using RetroDownfall.Arcanum.Core.Operations;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class LongRunningOperationStore
{
    internal static Task<LongRunningOperation> CreateWithinAsync(DbConnection connection, DbTransaction? transaction,
        LongRunningOperationCreateRequest request, CancellationToken ct) =>
        CreateWithinAsync(connection, transaction, Guid.NewGuid(), request, 0, null, null, ct);

    /// <summary>Creates the ledger identity and its first checkpoint in the caller-owned domain transaction.</summary>
    internal static async Task<LongRunningOperation> CreateWithinAsync(DbConnection connection, DbTransaction? transaction,
        Guid operationId, LongRunningOperationCreateRequest request, int checkpointVersion, byte[]? checkpointPayload,
        string? checkpointReference, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);

        ArgumentException.ThrowIfNullOrWhiteSpace(request.PublicSummary);

        if (!LongRunningOperationPolicyCatalog.IsRegistered(request.Kind, request.RecoveryPolicy))
        {
            throw new ArgumentException($"Operation kind '{request.Kind}' is not registered with recovery policy '{request.RecoveryPolicy}'.", nameof(request));
        }

        string kind = Bound(request.Kind, MaxKindLength);

        string summary = Bound(request.PublicSummary, MaxSummaryLength);

        await using DbCommand cmd = connection.CreateCommand();

        cmd.Transaction = transaction;

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
                 0, @checkpoint, @payload, @reference, @summary, NULL, 0)
            """;

        Add(cmd, "@id", Format(operationId));

        Add(cmd, "@kind", kind);

        Add(cmd, "@state", (int)LongRunningOperationState.Pending);

        Add(cmd, "@policy", (int)request.RecoveryPolicy);

        Add(cmd, "@root", FormatNullable(request.RootOperationId));

        Add(cmd, "@parent", FormatNullable(request.ParentOperationId));

        Add(cmd, "@session", FormatReferenceNullable(request.SessionId));

        Add(cmd, "@run", FormatReferenceNullable(request.RunId));

        Add(cmd, "@inference", FormatReferenceNullable(request.InferenceRunId));

        Add(cmd, "@reservation", FormatNullable(request.BudgetReservationId));

        Add(cmd, "@claim", FormatNullable(request.IdempotencyClaimId));

        _ = UtcInstantSql.AddParameter(cmd, "@created", request.CreatedAt);

        Add(cmd, "@checkpoint", checkpointVersion);

        Add(cmd, "@payload", checkpointPayload);

        Add(cmd, "@reference", checkpointReference);

        Add(cmd, "@summary", summary);

        _ = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new LongRunningOperation(
            operationId,
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
            CheckpointVersion: checkpointVersion,
            CheckpointPayload: checkpointPayload,
            CheckpointReference: checkpointReference,
            summary,
            TerminalErrorCode: null,
            Revision: 0);
    }

    /// <summary>
    /// Applies the ledger's ordinary revision/owner CAS within a caller-owned domain transaction.
    /// Callers requiring a live lease must verify its expiry on this same transaction before invoking it.
    /// </summary>
    internal static async Task<bool> TryTransitionWithinAsync(DbConnection connection, DbTransaction? transaction,
        Guid operationId, long expectedRevision, string? ownerId, LongRunningOperationState state,
        DateTimeOffset utcNow, string? terminalErrorCode, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        ConfigureTransitionCommand(command, operationId, expectedRevision, ownerId, state, utcNow, terminalErrorCode);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    internal static async Task<LongRunningOperation?> ReadWithinAsync(DbConnection connection, DbTransaction transaction,
        Guid operationId, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = $"SELECT {SelectColumns} FROM LongRunningOperations WHERE Id = @id;";

        Add(command, "@id", Format(operationId));

        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }
}
