using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Mcp;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

internal sealed class SqliteToolInvocationStore(IGrimoireOrdinaryConnectionFactory connections, TimeProvider time) : IToolInvocationStore
{
    private const int InterruptedEffectPageSize = 16;

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
                VALUES($receipt,$session,$assistant,$claim,$round,$call,$digest,$createdAtUtcTicks,1)
                """);

            Add(insert, "$claim", claim is { } id ? Id(id) : null);

            Add(insert, "$createdAtUtcTicks", now.UtcTicks);

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

            var proof = await ReadCommittedResultAsync(connection, tx, key.SessionId, existing.Evidence,
                key.ArgumentsDigest.Bytes.ToArray(), token).ConfigureAwait(false);

            if (proof.UnresolvedPatch) return Conflict;

            if (existing.Evidence.State == ToolInvocationState.Completed)
                return existing.Evidence.ResultEntryId == resultEntryId ? true : Conflict;

            if (proof.ResultEntryId != resultEntryId) return Conflict;

            await using SqliteCommand update = KeyCommand(connection, tx, key, """
                UPDATE session_tool_invocations SET StateCode=3,ResultEntryId=$result
                WHERE ReceiptId=$receipt AND SessionId=$session AND StateCode=2
                """);

            Add(update, "$result", Id(resultEntryId));

            return await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1 ? true : Unavailable;
        }, ct).ConfigureAwait(false);

        return confirmed.IsFailure ? Result.Failure(confirmed.Error) : Result.Success();
    }

    internal static async Task<bool> CompleteProtectedWithinAsync(SqliteConnection connection, SqliteTransaction tx,
        SessionTurnClaimLease claim, ToolInvocationCompletion completion, string toolName, string arguments,
        Guid resultEntryId, CancellationToken ct)
    {
        ToolInvocationKey key = completion.Key;

        if (!Valid(key) || resultEntryId == Guid.Empty || resultEntryId == key.Receipt.ResultEntryId
            || completion.DispatchId == Guid.Empty || claim.Claim.SessionId != key.SessionId
            || claim.FutureAssistantEntryId != key.AssistantEntryId || completion.Identity.ToolName != toolName)
        {
            return false;
        }

        ToolInvocationKey observed = ToolInvocationKey.Capture(key.SessionId, key.AssistantEntryId, completion.Identity, arguments);

        if (observed != key) return false;

        await using SqliteCommand update = KeyCommand(connection, tx, key, """
            UPDATE session_tool_invocations SET StateCode=3,ResultEntryId=$result
            WHERE SessionId=$session AND AssistantEntryId=$assistant AND ClaimId=$claim
              AND ReceiptId=$receipt AND RoundOrdinal=$round AND CallOrdinal=$call AND ArgumentsDigest=$digest
              AND StateCode=2 AND DispatchId=$dispatch
            """);

        Add(update, "$claim", Id(claim.Claim.ClaimId));
        Add(update, "$dispatch", Id(completion.DispatchId));
        Add(update, "$result", Id(resultEntryId));

        return await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
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

            return await ReadWithinAsync(lease.Connection, tx, sessionId, receiptId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or FormatException)
        {
            return Unavailable;
        }
    }

    public async Task<Result<ToolInvocationTurnProgress>> ReadTurnProgressAsync(Guid sessionId, Guid assistantEntryId, CancellationToken ct)
    {
        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using var lease = opened.Value;

        try
        {
            await using var tx = lease.Connection.BeginTransaction(deferred: true);

            return await ReadTurnProgressWithinAsync(lease.Connection, tx, sessionId, assistantEntryId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException or FormatException)
        {
            return Unavailable;
        }
    }

    internal static async Task<Result<InterruptedToolEffectPage>> ReadInterruptedEffectsWithinAsync(
        SqliteConnection connection, SqliteTransaction tx, Guid sessionId, Guid? after,
        Guid? currentBoot, Guid? currentInstallation, CancellationToken ct)
    {
        long? afterTicks = null;

        if (after is { } cursor)
        {
            await using SqliteCommand lookup = connection.CreateCommand();

            lookup.Transaction = tx;

            lookup.CommandText = "SELECT CreatedAtUtcTicks FROM session_tool_invocations WHERE ReceiptId=$receipt AND SessionId=$session;";

            Add(lookup, "$receipt", Id(cursor));

            Add(lookup, "$session", Id(sessionId));

            if (await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long ticks)
                return new Error(ErrorCodes.Validation.InvalidFields, "The interrupted tool cursor does not belong to this Session.");

            afterTicks = ticks;
        }

        List<(Guid Receipt, bool ProvenOwner)> candidates = [];

        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = tx;

            // Keep the interrupted claim lookup first even on databases without planner statistics.
            read.CommandText = """
                SELECT i.ReceiptId,c.StateCode,c.OwnerBootId,c.ExecutorId
                FROM session_turn_claims c
                CROSS JOIN session_tool_invocations i ON c.ClaimId=i.ClaimId AND c.SessionId=i.SessionId AND c.AssistantEntryId=i.AssistantEntryId
                WHERE c.SessionId=$session AND i.StateCode=2 AND c.StateCode IN (2,4,6)
                  AND (c.StateCode IN (4,6) OR (c.StateCode=2 AND c.OriginInstallationId=$installation AND c.OriginRestoreEpoch=0
                    AND c.OwnerBootId<>$boot AND c.ExecutorId IS NOT NULL AND c.LeaseDeadlineUtc IS NOT NULL))
                  AND ($after IS NULL OR (i.CreatedAtUtcTicks,i.ReceiptId)>($afterTicks,$after))
                ORDER BY i.CreatedAtUtcTicks,i.ReceiptId
                LIMIT $limit;
                """;

            Add(read, "$session", Id(sessionId));

            Add(read, "$installation", currentInstallation is { } target ? Id(target) : null);

            Add(read, "$boot", currentBoot is { } owner ? Id(owner) : null);

            Add(read, "$after", after is { } previousPage ? Id(previousPage) : null);

            Add(read, "$afterTicks", afterTicks);

            Add(read, "$limit", InterruptedEffectPageSize + 1);

            await using SqliteDataReader row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await row.ReadAsync(ct).ConfigureAwait(false))
            {
                bool provenOwner = (SessionTurnClaimState)row.GetInt32(1) is SessionTurnClaimState.Discarded or SessionTurnClaimState.RestoredInterrupted
                    || (Guid.TryParseExact(row.GetString(2), "D", out Guid previous) && previous != Guid.Empty && previous != currentBoot
                        && Guid.TryParseExact(row.GetString(3), "D", out Guid executor) && executor != Guid.Empty);

                candidates.Add((Guid.ParseExact(row.GetString(0), "D"), provenOwner));
            }
        }

        List<ToolInvocationEvidence> outstanding = [];

        foreach ((Guid receipt, bool provenOwner) in candidates.Take(InterruptedEffectPageSize))
        {
            if (!provenOwner) continue;

            var evidence = await ReadWithinAsync(connection, tx, sessionId, receipt, ct).ConfigureAwait(false);

            if (evidence.IsFailure) return evidence.Error;

            if (evidence.Value is { State: ToolInvocationState.Dispatched } unresolved) outstanding.Add(unresolved);
        }

        Guid? next = candidates.Count > InterruptedEffectPageSize ? candidates[InterruptedEffectPageSize - 1].Receipt : null;

        return new InterruptedToolEffectPage([.. outstanding], next);
    }

    internal static async Task<Result<ToolInvocationTurnProgress>> ReadTurnProgressWithinAsync(SqliteConnection connection, SqliteTransaction tx,
        Guid sessionId, Guid assistantEntryId, CancellationToken ct)
    {
        List<(ToolInvocationEvidence Evidence, byte[] Digest)> receipts = [];

        await using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = tx;

            read.CommandText = """
                SELECT ReceiptId,AssistantEntryId,RoundOrdinal,CallOrdinal,StateCode,CreatedAtUtcTicks,ResultEntryId,ArgumentsDigest
                FROM session_tool_invocations WHERE SessionId=$session AND AssistantEntryId=$assistant AND StateCode IN(2,3)
                ORDER BY RoundOrdinal DESC,CallOrdinal DESC
                """;

            Add(read, "$session", Id(sessionId));
            Add(read, "$assistant", Id(assistantEntryId));

            await using SqliteDataReader row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await row.ReadAsync(ct).ConfigureAwait(false))
            {
                if (row.GetValue(7) is not byte[] digest || digest.Length != 32) return Unavailable;

                receipts.Add((ReadEvidence(row), digest));
            }
        }

        Guid? outstanding = null;

        Guid? lastVerified = null;

        foreach ((ToolInvocationEvidence evidence, byte[] digest) in receipts)
        {
            var proof = await ReadCommittedResultAsync(connection, tx, sessionId, evidence, digest, ct).ConfigureAwait(false);

            if (proof.ResultEntryId is not null)
                lastVerified ??= evidence.ReceiptId;
            else if (proof.UnresolvedPatch || evidence.State == ToolInvocationState.Dispatched)
                outstanding = evidence.ReceiptId;
        }

        return new ToolInvocationTurnProgress(outstanding, lastVerified);
    }

    internal static async Task<Result<ToolInvocationEvidence?>> ReadWithinAsync(SqliteConnection connection, SqliteTransaction tx,
        Guid sessionId, Guid receiptId, CancellationToken ct)
    {
        await using SqliteCommand read = connection.CreateCommand();

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
            var proof = await ReadCommittedResultAsync(connection, tx, sessionId, evidence, digest, ct).ConfigureAwait(false);

            evidence = evidence with
            {
                State = proof.UnresolvedPatch ? ToolInvocationState.Dispatched
                    : proof.ResultEntryId is not null ? ToolInvocationState.Completed : evidence.State,
                ResultEntryId = proof.ResultEntryId,
            };
        }

        return evidence;
    }

    private static async Task<(Guid? ResultEntryId, bool UnresolvedPatch)> ReadCommittedResultAsync(SqliteConnection connection, SqliteTransaction tx, Guid session,
        ToolInvocationEvidence evidence, byte[] digest, CancellationToken ct)
    {
        Guid call = ToolInteractionReceiptDerivation.DeriveCallEntryId(evidence.ReceiptId);

        Guid result = ToolInteractionReceiptDerivation.DeriveResultEntryId(evidence.ReceiptId);

        if (evidence is { State: ToolInvocationState.Completed, ResultEntryId: { } ownerEntry }
            && ownerEntry != result)
        {
            return (await ReadProtectedResultReferenceAsync(connection, tx, session, evidence.ReceiptId, ownerEntry, ct)
                .ConfigureAwait(false), false);
        }

        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = tx;

        read.CommandText = """
            SELECT c.ToolName,c.ToolCallId,c.ToolArguments,c.CreatedAt,
                   CASE WHEN c.ToolName=$patch THEN substr(r.Content,14,length(r.Content)-14) END
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

        Add(read, "$patch", ToolRiskClassifier.ApplyPatchToolName);

        await using SqliteDataReader row = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await row.ReadAsync(ct).ConfigureAwait(false) || UtcInstantText.Parse(row.GetString(3)) != evidence.CreatedAt) return default;

        ToolInvocationIdentity identity = new(evidence.AssistantEntryId.ToString("D"), row.IsDBNull(1) ? null : row.GetString(1),
            evidence.Round, evidence.Call, row.GetString(0));

        ToolInvocationKey key = ToolInvocationKey.Capture(session, evidence.AssistantEntryId, identity, row.GetString(2));

        if (key.Receipt.Id != evidence.ReceiptId || !key.ArgumentsDigest.Bytes.SequenceEqual(digest)) return default;

        if (identity.ToolName == ToolRiskClassifier.ApplyPatchToolName
            && ApplyPatchToolExecutionService.ReceiptIsUnresolved(ApplyPatchToolExecutionService.ReadReceipt(row.GetString(4))))
            return (null, true);

        return (result, false);
    }

    private static async Task<Guid?> ReadProtectedResultReferenceAsync(SqliteConnection connection, SqliteTransaction tx,
        Guid session, Guid receiptId, Guid resultEntryId, CancellationToken ct)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = tx;

        read.CommandText = """
            SELECT 1 FROM Entries e
            JOIN artifact_sensitivity l ON l.ArtifactId=e.Id AND l.SessionId=e.SessionId
            JOIN session_tool_invocations t ON t.ReceiptId=$receipt AND t.SessionId=e.SessionId AND t.ClaimId=l.TurnId
            WHERE e.Id=$result AND e.SessionId=$session AND e.Role=$system AND l.ArtifactKindCode=$entryKind
            """;

        Add(read, "$session", Id(session));
        Add(read, "$receipt", Id(receiptId));
        Add(read, "$result", Id(resultEntryId));
        Add(read, "$system", (int)MessageRole.System);
        Add(read, "$entryKind", (int)SensitiveArtifactKind.AssistantEntry);

        return await read.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null ? resultEntryId : null;
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
            && !row.IsDBNull(4) && (owner.HoldsActiveExecution ?? (UtcInstantText.Parse(row.GetString(4)) >= now));
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
