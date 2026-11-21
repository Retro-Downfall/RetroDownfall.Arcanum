using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

internal sealed class SqliteToolInvocationStore(IGrimoireOrdinaryConnectionFactory connections, TimeProvider time) : IToolInvocationStore
{
    public Task<Result<ToolInvocationEvidence>> PrepareAsync(ToolInvocationKey key, SessionTurnClaimLease? owner, CancellationToken ct) =>
        WriteAsync<ToolInvocationEvidence>(async (connection, tx, token) =>
        {
            if (!Valid(key)) return Conflict;

            DateTimeOffset now = time.GetUtcNow();

            if (!await OwnsActiveTurnAsync(connection, tx, key, owner, now, token).ConfigureAwait(false)) return Interrupted;

            var prior = await ReadSlotAsync(connection, tx, key, token).ConfigureAwait(false);

            if (prior.IsFailure) return prior.Error;

            Guid? claim = owner?.Claim.ClaimId;

            if (prior.Value is { } existing) return existing.ClaimId == claim ? existing.Evidence : Conflict;

            await using SqliteCommand insert = KeyCommand(connection, tx, key, """
                INSERT INTO session_tool_invocations(ReceiptId,SessionId,AssistantEntryId,ClaimId,RoundOrdinal,CallOrdinal,ArgumentsDigest,CreatedAtUtcTicks,StateCode)
                VALUES($receipt,$session,$assistant,$claim,$round,$call,$digest,$now,1)
                """);

            Add(insert, "$claim", claim is { } id ? Id(id) : null);

            Add(insert, "$now", now.UtcTicks);

            if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Unavailable;

            return new ToolInvocationEvidence(key.Receipt.Id, key.AssistantEntryId, key.Round, key.Call,
                ToolInvocationState.Prepared, now, null);
        }, ct);

    public Task<Result<ToolInvocationDispatch>> AdmitAsync(ToolInvocationKey key, SessionTurnClaimLease? owner, CancellationToken ct) =>
        WriteAsync<ToolInvocationDispatch>(async (connection, tx, token) =>
        {
            if (!Valid(key)) return Conflict;

            if (!await OwnsActiveTurnAsync(connection, tx, key, owner, time.GetUtcNow(), token).ConfigureAwait(false)) return Interrupted;

            var prior = await ReadSlotAsync(connection, tx, key, token).ConfigureAwait(false);

            if (prior.IsFailure) return prior.Error;

            if (prior.Value is not { } existing || existing.ClaimId != owner?.Claim.ClaimId) return Conflict;

            if (existing.Evidence.State != ToolInvocationState.Prepared) return new ToolInvocationDispatch(existing.Evidence, null);

            Guid dispatch = Guid.NewGuid();

            await using SqliteCommand update = KeyCommand(connection, tx, key, """
                UPDATE session_tool_invocations SET StateCode=2,DispatchId=$dispatch
                WHERE ReceiptId=$receipt AND SessionId=$session AND StateCode=1
                """);

            Add(update, "$dispatch", Id(dispatch));

            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;

            return new ToolInvocationDispatch(existing.Evidence with { State = ToolInvocationState.Dispatched }, dispatch);
        }, ct);

    public async Task<Result> ReleaseUndispatchedAsync(ToolInvocationKey key, Guid dispatchId, SessionTurnClaimLease? owner, CancellationToken ct)
    {
        var released = await WriteAsync<bool>(async (connection, tx, token) =>
        {
            if (!Valid(key) || dispatchId == Guid.Empty) return Conflict;

            if (!await OwnsActiveTurnAsync(connection, tx, key, owner, time.GetUtcNow(), token).ConfigureAwait(false)) return Interrupted;

            await using SqliteCommand update = KeyCommand(connection, tx, key, """
                UPDATE session_tool_invocations SET StateCode=1,DispatchId=NULL
                WHERE ReceiptId=$receipt AND SessionId=$session AND AssistantEntryId=$assistant AND ArgumentsDigest=$digest
                  AND RoundOrdinal=$round AND CallOrdinal=$call AND StateCode=2 AND DispatchId=$dispatch
                """);

            Add(update, "$dispatch", Id(dispatchId));

            return await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1 ? true : Conflict;
        }, ct).ConfigureAwait(false);

        return released.IsFailure ? released.Error : Result.Success();
    }

    public async Task<Result> ConfirmAsync(ToolInvocationKey key, Guid resultEntryId, CancellationToken ct)
    {
        var confirmed = await WriteAsync<bool>(async (connection, tx, token) =>
        {
            if (!Valid(key) || resultEntryId != key.Receipt.ResultEntryId) return Conflict;

            var prior = await ReadSlotAsync(connection, tx, key, token).ConfigureAwait(false);

            if (prior.IsFailure) return prior.Error;

            if (prior.Value is not { } existing || existing.Evidence.State == ToolInvocationState.Prepared) return Conflict;

            if (existing.Evidence.State == ToolInvocationState.Completed)
                return existing.Evidence.ResultEntryId == resultEntryId ? true : Conflict;

            await using SqliteCommand update = KeyCommand(connection, tx, key, """
                UPDATE session_tool_invocations SET StateCode=3,ResultEntryId=$result
                WHERE ReceiptId=$receipt AND SessionId=$session AND StateCode=2
                  AND EXISTS(SELECT 1 FROM Entries WHERE Id=$callEntry AND SessionId=$session AND Role=$assistantRole)
                  AND EXISTS(SELECT 1 FROM Entries WHERE Id=$result AND SessionId=$session AND Role=$resultRole)
                """);

            Add(update, "$callEntry", Id(key.Receipt.CallEntryId));

            Add(update, "$result", Id(resultEntryId));

            Add(update, "$assistantRole", (int)MessageRole.Assistant);

            Add(update, "$resultRole", (int)MessageRole.System);

            return await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1 ? true : Unavailable;
        }, ct).ConfigureAwait(false);

        return confirmed.IsFailure ? Result.Failure(confirmed.Error) : Result.Success();
    }

    public async Task<Result<ToolInvocationEvidence?>> ReadAsync(Guid sessionId, Guid receiptId, CancellationToken ct)
    {
        if (sessionId == Guid.Empty || receiptId == Guid.Empty) return Conflict;

        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using var lease = opened.Value;

        try
        {
            await using var tx = lease.Connection.BeginTransaction(deferred: true);

            await using SqliteCommand read = lease.Connection.CreateCommand();

            read.Transaction = tx;

            read.CommandText = """
                SELECT ReceiptId,AssistantEntryId,RoundOrdinal,CallOrdinal,StateCode,CreatedAtUtcTicks,ResultEntryId,ArgumentsDigest
                FROM session_tool_invocations WHERE SessionId=$session AND ReceiptId=$receipt
                """;

            Add(read, "$session", Id(sessionId));

            Add(read, "$receipt", Id(receiptId));

            ToolInvocationEvidence evidence;

            byte[] digest;

            await using (SqliteDataReader row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await row.ReadAsync(ct).ConfigureAwait(false)) return Result<ToolInvocationEvidence?>.Success(null);

                evidence = ReadEvidence(row);

                if (row.GetValue(7) is not byte[] bytes || bytes.Length != 32) return Unavailable;

                digest = bytes;
            }

            if (evidence.State is ToolInvocationState.Dispatched or ToolInvocationState.Completed)
            {
                Guid? receipt = await ReadCommittedResultAsync(lease.Connection, tx, sessionId, evidence, digest, ct).ConfigureAwait(false);

                evidence = evidence with
                {
                    State = receipt is not null ? ToolInvocationState.Completed : evidence.State,
                    ResultEntryId = receipt,
                };
            }

            return evidence;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or FormatException)
        {
            return Unavailable;
        }
    }

    private static async Task<Guid?> ReadCommittedResultAsync(SqliteConnection connection, SqliteTransaction tx, Guid session,
        ToolInvocationEvidence evidence, byte[] digest, CancellationToken ct)
    {
        Guid call = ToolInteractionReceiptDerivation.DeriveCallEntryId(evidence.ReceiptId);

        Guid result = ToolInteractionReceiptDerivation.DeriveResultEntryId(evidence.ReceiptId);

        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = tx;

        read.CommandText = """
            SELECT c.ToolName,c.ToolCallId,c.ToolArguments,c.CreatedAt
            FROM Entries c JOIN Entries r ON r.Id=$result AND r.SessionId=c.SessionId
            WHERE c.Id=$call AND c.SessionId=$session AND c.Role=$assistant AND r.Role=$system
              AND length(c.ToolName) BETWEEN 1 AND 256 AND (c.ToolCallId IS NULL OR length(c.ToolCallId)<=256)
              AND c.ToolArguments IS NOT NULL AND c.Content=('[ToolCall: ' || c.ToolName || '(' || c.ToolArguments || ')]')
              AND c.ModelUsed=r.ModelUsed AND c.CreatedAt=r.CreatedAt AND c.IsPinned=0 AND r.IsPinned=0
              AND r.ToolName IS NULL AND r.ToolArguments IS NULL AND r.ToolCallId IS NULL
              AND substr(r.Content,1,13)='[ToolResult: ' AND substr(r.Content,-1)=']'
            """;

        Add(read, "$call", Id(call));

        Add(read, "$result", Id(result));

        Add(read, "$session", Id(session));

        Add(read, "$assistant", (int)MessageRole.Assistant);

        Add(read, "$system", (int)MessageRole.System);

        await using SqliteDataReader row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await row.ReadAsync(ct).ConfigureAwait(false) || UtcInstantText.Parse(row.GetString(3)) != evidence.CreatedAt) return null;

        ToolInvocationIdentity identity = new(evidence.AssistantEntryId.ToString("D"), row.IsDBNull(1) ? null : row.GetString(1),
            evidence.Round, evidence.Call, row.GetString(0));

        ToolInvocationKey key = ToolInvocationKey.Capture(session, evidence.AssistantEntryId, identity, row.GetString(2));

        return key.Receipt.Id == evidence.ReceiptId && key.ArgumentsDigest.Bytes.SequenceEqual(digest) ? result : null;
    }

    private async Task<Result<T>> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> write, CancellationToken ct)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadWrite, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using var lease = opened.Value;

        try
        {
            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                await using SqliteTransaction tx = lease.Connection.BeginTransaction(deferred: false);

                Result<T> result = await write(lease.Connection, tx, ct).ConfigureAwait(false);

                if (result.IsSuccess) await tx.CommitAsync(ct).ConfigureAwait(false);

                return result;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or FormatException)
        {
            return Unavailable;
        }
    }

    private static async Task<bool> OwnsActiveTurnAsync(SqliteConnection connection, SqliteTransaction tx,
        ToolInvocationKey key, SessionTurnClaimLease? owner, DateTimeOffset now, CancellationToken ct)
    {
        await using SqliteCommand read = KeyCommand(connection, tx, key, """
            SELECT c.ClaimId,c.StateCode,c.ExecutorId,c.OwnerBootId,c.LeaseDeadlineUtc
            FROM Entries a JOIN Sessions s ON s.Id=a.SessionId
            LEFT JOIN session_turn_claims c ON c.AssistantEntryId=a.Id AND c.SessionId=s.Id
            WHERE a.Id=$assistant AND s.Id=$session AND a.Role=$role AND a.ToolName IS NULL
              AND NOT EXISTS(SELECT 1 FROM assistant_entry_finalizations f WHERE f.AssistantEntryId=a.Id)
            """);

        Add(read, "$role", (int)MessageRole.Assistant);

        await using var row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await row.ReadAsync(ct).ConfigureAwait(false)) return false;

        if (row.IsDBNull(0)) return owner is null;

        return owner is { IsExecutable: true, ExecutorId: { } executor, OwnerBootId: { } boot }
            && owner.Claim.SessionId == key.SessionId && owner.FutureAssistantEntryId == key.AssistantEntryId
            && row.GetString(0) == Id(owner.Claim.ClaimId) && row.GetInt32(1) == (int)SessionTurnClaimState.Begun
            && !row.IsDBNull(2) && row.GetString(2) == Id(executor) && !row.IsDBNull(3) && row.GetString(3) == Id(boot)
            && !row.IsDBNull(4) && UtcInstantText.Parse(row.GetString(4)) >= now;
    }

    private static async Task<Result<Slot?>> ReadSlotAsync(SqliteConnection connection, SqliteTransaction tx, ToolInvocationKey key, CancellationToken ct)
    {
        await using SqliteCommand read = KeyCommand(connection, tx, key, """
            SELECT ReceiptId,AssistantEntryId,RoundOrdinal,CallOrdinal,StateCode,CreatedAtUtcTicks,ResultEntryId,ClaimId,ArgumentsDigest
            FROM session_tool_invocations WHERE SessionId=$session AND AssistantEntryId=$assistant AND RoundOrdinal=$round AND CallOrdinal=$call
            """);

        await using SqliteDataReader row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await row.ReadAsync(ct).ConfigureAwait(false)) return Result<Slot?>.Success(null);

        if (row.GetString(0) != Id(key.Receipt.Id) || row.GetValue(8) is not byte[] digest || !digest.AsSpan().SequenceEqual(key.ArgumentsDigest.Bytes))
            return Conflict;

        return new Slot(ReadEvidence(row), row.IsDBNull(7) ? null : Guid.ParseExact(row.GetString(7), "D"));
    }

    private static ToolInvocationEvidence ReadEvidence(SqliteDataReader row) => new(
        Guid.ParseExact(row.GetString(0), "D"), Guid.ParseExact(row.GetString(1), "D"), row.GetInt32(2), row.GetInt32(3),
        (ToolInvocationState)row.GetInt32(4), new DateTimeOffset(row.GetInt64(5), TimeSpan.Zero),
        row.IsDBNull(6) ? null : Guid.ParseExact(row.GetString(6), "D"));

    private static bool Valid(ToolInvocationKey key) => key.SessionId != Guid.Empty && key.AssistantEntryId != Guid.Empty
        && key.Round >= 0 && key.Call >= 0 && key.ArgumentsDigest.Bytes.Length == 32 && key.Receipt.Id != Guid.Empty
        && key.Receipt.CallEntryId == ToolInteractionReceiptDerivation.DeriveCallEntryId(key.Receipt.Id)
        && key.Receipt.ResultEntryId == ToolInteractionReceiptDerivation.DeriveResultEntryId(key.Receipt.Id);

    private static SqliteCommand KeyCommand(SqliteConnection connection, SqliteTransaction tx, ToolInvocationKey key, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = tx;

        command.CommandText = sql;

        Add(command, "$receipt", Id(key.Receipt.Id));

        Add(command, "$session", Id(key.SessionId));

        Add(command, "$assistant", Id(key.AssistantEntryId));

        Add(command, "$round", key.Round);

        Add(command, "$call", key.Call);

        Add(command, "$digest", key.ArgumentsDigest.Bytes.ToArray());

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();

    private static Error Conflict => new(ErrorCodes.Security.IdempotencyConflict, "The Session tool slot is bound to a different invocation.");

    private static Error Interrupted => new(ErrorCodes.Hub.SessionTurnRestoredInterrupted, "The Session turn no longer owns this tool invocation.");

    private static Error Unavailable => new(ErrorCodes.Hub.Error, "The Session tool invocation evidence is unavailable.");

    private sealed record Slot(ToolInvocationEvidence Evidence, Guid? ClaimId);
}
