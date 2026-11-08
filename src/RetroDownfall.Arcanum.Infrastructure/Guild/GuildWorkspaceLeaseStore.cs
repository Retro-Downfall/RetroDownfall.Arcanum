using Microsoft.Data.Sqlite;
using System.Text;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Transaction participant; the caller owns authenticated root and physical-scope admission.</summary>
internal sealed partial class GuildWorkspaceLeaseStore(IGrimoireOrdinaryConnectionFactory connections)
{
    internal const int MaximumIdentityBytes = 1024 * 1024;

    private const string Columns = "Id,OwnerKindCode,PrincipalId,QuestId,RootQuestId,SessionId,CampaignId,WorkspaceId,InstallationGeneration,WorkspaceRoot,IdentityJson,IdentityDigest,HostInstanceId,Epoch,Revision,StateCode,HeartbeatAtUtc,ExpiresAtUtc,InFlightMutationCount,EffectKindCode,ReasonCode,IdempotencyKey,RequestDigest";

    private static readonly string BoundedColumns = string.Join(',', Columns.Split(',').Select(column => column switch
    {
        "IdentityJson" => "CASE WHEN typeof(IdentityJson)='text' AND length(CAST(IdentityJson AS BLOB)) BETWEEN 1 AND 1048576 THEN IdentityJson ELSE NULL END",
        "WorkspaceRoot" => "CASE WHEN typeof(WorkspaceRoot)='text' AND length(CAST(WorkspaceRoot AS BLOB)) BETWEEN 1 AND 32768 THEN WorkspaceRoot ELSE NULL END",
        "ReasonCode" => "CASE WHEN typeof(ReasonCode)='text' AND length(CAST(ReasonCode AS BLOB)) BETWEEN 1 AND 128 THEN ReasonCode ELSE NULL END",
        "OwnerKindCode" or "Epoch" or "Revision" or "StateCode" or "InFlightMutationCount" or "EffectKindCode" => $"CASE WHEN typeof({column})='integer' THEN {column} ELSE NULL END",
        "IdentityDigest" or "RequestDigest" => $"CASE WHEN typeof({column})='text' AND length(CAST({column} AS BLOB))=64 THEN {column} ELSE NULL END",
        "HeartbeatAtUtc" or "ExpiresAtUtc" => $"CASE WHEN typeof({column})='text' AND length(CAST({column} AS BLOB))=28 THEN {column} ELSE NULL END",
        _ => $"CASE WHEN typeof({column})='text' AND length(CAST({column} AS BLOB))=36 THEN {column} ELSE NULL END",
    })) + ",CASE WHEN " + string.Join(" AND ", new[] { "QuestId", "RootQuestId", "SessionId", "CampaignId" }
        .Select(column => $"({column} IS NULL OR (typeof({column})='text' AND length(CAST({column} AS BLOB))=36))"))
        + " AND (ReasonCode IS NULL OR (typeof(ReasonCode)='text' AND length(CAST(ReasonCode AS BLOB)) BETWEEN 1 AND 128)) THEN 1 ELSE 0 END";

    internal Task<Result<T>> InTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken ct) => ExecuteAsync(true, action, ct);

    internal Task<Result<T>> ReadTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken ct) =>
        ExecuteAsync(false, action, ct);

    private async Task<Result<T>> ExecuteAsync<T>(bool write, Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        try
        {
            var opened = await connections.OpenFreshAsync(write ? GrimoireOrdinaryFreshConnectionKind.ReadWrite : GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

            if (opened.IsFailure) return opened.Error;

            await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

            await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: !write);

            Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, transaction, ct).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            Result<T> result = await action(lease.Connection, transaction, ct).ConfigureAwait(false);

            if (result.IsSuccess) await transaction.CommitAsync(ct).ConfigureAwait(false);

            return result;
        }
        catch (SqliteException exception)
        {
            return exception.SqliteErrorCode is 5 or 6 or 13
                ? new Error(ErrorCodes.Guild.StoragePressure, "Guild workspace storage could not admit this operation.")
                : exception.SqliteErrorCode == 19 ? Conflict : Unavailable;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            return Unavailable;
        }
    }

    internal static async Task<Result<GuildWorkspaceLeaseRecord>> InsertWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildWorkspaceLeaseRecord row, CancellationToken ct)
    {
        if (!Valid(row) || row.State != GuildWorkspaceLeaseState.Held || row.Epoch != 1 || row.Revision != 1 || row.InFlightMutationCount != 0) return Invalid;

        if (await MutationKeyExistsWithinAsync(connection, transaction, row.Owner, row.IdempotencyKey, ct).ConfigureAwait(false)) return IdempotencyConflict;

        await using SqliteCommand command = Command(connection, transaction, $"INSERT INTO guild_workspace_leases({Columns}) VALUES({string.Join(',', Columns.Split(',').Select(column => '$' + column))})");

        object?[] values = [Id(row.Id), (int)row.Owner.Kind, Id(row.Owner.PrincipalId), Id(row.Owner.QuestId), Id(row.Owner.RootQuestId), Id(row.Owner.SessionId), Id(row.Owner.CampaignId), Id(row.Owner.WorkspaceId), Id(row.Owner.InstallationGeneration), row.WorkspaceRoot, row.IdentityJson, row.IdentityDigest, Id(row.HostInstanceId), row.Epoch, row.Revision, (int)row.State, UtcInstantText.Format(row.HeartbeatAt), UtcInstantText.Format(row.ExpiresAt), row.InFlightMutationCount, (int)row.EffectKind, row.ReasonCode, Id(row.IdempotencyKey), row.RequestDigest];

        string[] names = Columns.Split(',');

        for (int index = 0; index < names.Length; index++) Add(command, '$' + names[index], values[index]);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        Result<GuildWorkspaceLeaseMutationReceipt> accepted = await RecordMutationWithinAsync(connection, transaction, row,
            new(row.IdempotencyKey, row.RequestDigest), GuildAuthorityAction.LeaseAcquire, row.HeartbeatAt, ct).ConfigureAwait(false);

        return accepted.IsSuccess ? row : accepted.Error;
    }

    internal static async Task<Result<GuildWorkspaceLeaseRecord>> FindReplayWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildWorkspaceLeaseOwner owner, Guid key, string digest, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, $"SELECT {BoundedColumns} FROM guild_workspace_leases WHERE PrincipalId=$principal AND QuestId IS $quest AND IdempotencyKey=$key");

        Add(command, "$principal", Id(owner.PrincipalId));

        Add(command, "$quest", Id(owner.QuestId));

        Add(command, "$key", Id(key));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);

            return await MutationKeyExistsWithinAsync(connection, transaction, owner, key, ct).ConfigureAwait(false) ? IdempotencyConflict : NotFound;
        }

        Result<GuildWorkspaceLeaseRecord> found = Decode(reader);

        if (found.IsFailure) return found.Error;

        return found.Value.Owner == owner && found.Value.RequestDigest == digest ? found : IdempotencyConflict;
    }

    internal static async Task<Result<GuildWorkspaceLeaseRecord>> ReadWithinAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, $"SELECT {BoundedColumns} FROM guild_workspace_leases WHERE Id=$id");

        Add(command, "$id", Id(id));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Decode(reader) : NotFound;
    }

    internal static Task<Result> VisitProtectedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Func<GuildWorkspaceLeaseRecord, CancellationToken, Task<Result>> visit, CancellationToken ct) =>
        VisitWithinAsync(connection, transaction, true, visit, ct);

    internal static Task<Result> VisitAllWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Func<GuildWorkspaceLeaseRecord, CancellationToken, Task<Result>> visit, CancellationToken ct) =>
        VisitWithinAsync(connection, transaction, false, visit, ct);

    private static async Task<Result> VisitWithinAsync(SqliteConnection connection, SqliteTransaction transaction, bool protectedOnly,
        Func<GuildWorkspaceLeaseRecord, CancellationToken, Task<Result>> visit, CancellationToken ct)
    {
        // No lease-count ceiling: materialize only one bounded row at a time.
        await using SqliteCommand command = Command(connection, transaction, $"SELECT {BoundedColumns} FROM guild_workspace_leases"
            + (protectedOnly ? " WHERE StateCode IS NOT 3 OR InFlightMutationCount IS NOT 0" : string.Empty) + " ORDER BY Id");

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            Result<GuildWorkspaceLeaseRecord> row = Decode(reader);

            if (row.IsFailure) return row.Error;

            Result result = await visit(row.Value, ct).ConfigureAwait(false);

            if (result.IsFailure) return result;
        }

        return Result.Success();
    }

    internal static Task<Result<GuildWorkspaceLeaseRecord>> RenewWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildWorkspaceLeaseRecord expected, DateTimeOffset now, DateTimeOffset expiry, CancellationToken ct) =>
        expected.State != GuildWorkspaceLeaseState.Held || expected.ExpiresAt <= now || expiry <= now || expiry < expected.ExpiresAt
            ? Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(Conflict))
            : UpdateWithinAsync(connection, transaction, expected, expected with { HeartbeatAt = now, ExpiresAt = expiry }, ct);

    internal static Task<Result<GuildWorkspaceLeaseRecord>> DrainWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildWorkspaceLeaseRecord expected, string reason, CancellationToken ct) =>
        expected.State is GuildWorkspaceLeaseState.Released or GuildWorkspaceLeaseState.ReconciliationRequired
            ? Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(Conflict))
            : UpdateWithinAsync(connection, transaction, expected, expected with
            {
                State = expected.InFlightMutationCount == 0 ? GuildWorkspaceLeaseState.Released : GuildWorkspaceLeaseState.Draining,
                ReasonCode = reason,
            }, ct);

    internal static Task<Result<GuildWorkspaceLeaseRecord>> AdmitPermitWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildWorkspaceLeaseRecord expected, DateTimeOffset now, CancellationToken ct) =>
        expected.State != GuildWorkspaceLeaseState.Held || expected.ExpiresAt <= now || expected.InFlightMutationCount == long.MaxValue
            ? Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(Conflict))
            : UpdateWithinAsync(connection, transaction, expected, expected with { InFlightMutationCount = expected.InFlightMutationCount + 1 }, ct);

    internal static async Task<Result<GuildWorkspaceLeaseRecord>> FinishPermitWithinAsync(SqliteConnection connection, SqliteTransaction transaction, Guid id, long epoch, Guid host, bool uncertain, CancellationToken ct)
    {
        Result<GuildWorkspaceLeaseRecord> read = await ReadWithinAsync(connection, transaction, id, ct).ConfigureAwait(false);

        if (read.IsFailure) return read.Error;

        GuildWorkspaceLeaseRecord row = read.Value;

        if (row.Epoch != epoch || row.HostInstanceId != host || row.InFlightMutationCount <= 0) return Conflict;

        long remaining = row.InFlightMutationCount - 1;

        return await UpdateWithinAsync(connection, transaction, row, row with
        {
            InFlightMutationCount = remaining,
            State = uncertain ? GuildWorkspaceLeaseState.ReconciliationRequired
                : row.State == GuildWorkspaceLeaseState.Draining && remaining == 0 ? GuildWorkspaceLeaseState.Released : row.State,
            ReasonCode = uncertain ? ErrorCodes.Guild.ReconciliationRequired : row.ReasonCode,
        }, ct).ConfigureAwait(false);
    }

    internal static Task<Result<GuildWorkspaceLeaseRecord>> ReleaseDrainedWithinAsync(SqliteConnection connection, SqliteTransaction transaction, GuildWorkspaceLeaseRecord expected, CancellationToken ct) =>
        expected.InFlightMutationCount != 0 || expected.State != GuildWorkspaceLeaseState.Draining
            ? Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(Conflict))
            : UpdateWithinAsync(connection, transaction, expected, expected with { State = GuildWorkspaceLeaseState.Released }, ct);

    /// <summary>
    /// The broker must hold proof.Pin() around its entire owning InTransactionAsync call, including
    /// COMMIT. Only a quiescent ordinary row can transfer: a host-stop proof says nothing about
    /// uncertain artifacts, and this slice has no artifact-owner recovery evidence. Child processes
    /// likewise require their own complete authenticated extinction proof.
    /// </summary>
    internal static Task<Result<GuildWorkspaceLeaseRecord>> RecoverWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildWorkspaceLeaseRecord expected, GuildWorkspaceLeaseHostLock.ManagedHostStopProof proof, CancellationToken ct)
    {
        if (proof is null || proof.Validate().IsFailure)
            return Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(Unavailable));

        Guid newHost = proof.HostInstanceId;

        return expected.EffectKind != GuildWorkspaceLeaseEffectKind.Managed || expected.InFlightMutationCount != 0
            || expected.State is not (GuildWorkspaceLeaseState.Held or GuildWorkspaceLeaseState.Draining) || newHost == Guid.Empty
            || newHost == expected.HostInstanceId || expected.Epoch == long.MaxValue
            ? Task.FromResult(Result<GuildWorkspaceLeaseRecord>.Failure(new(ErrorCodes.Guild.ReconciliationRequired, "Workspace ownership requires authenticated extinction and artifact reconciliation evidence.")))
            : UpdateWithinAsync(connection, transaction, expected, expected with
            {
                HostInstanceId = newHost,
                Epoch = expected.Epoch + 1,
                State = GuildWorkspaceLeaseState.Released,
                InFlightMutationCount = 0,
                ReasonCode = "managed-quiescent-host-recovered",
            }, ct);
    }

    internal static async Task<Result<Guid>> GetNativeOperatorPrincipalWithinAsync(SqliteConnection connection, SqliteTransaction transaction, Guid installationGeneration, CancellationToken ct)
    {
        if (installationGeneration == Guid.Empty) return Invalid;

        await using SqliteCommand command = Command(connection, transaction, "INSERT INTO guild_workspace_authority_state(StateKey,InstallationGeneration,NativeOperatorPrincipalId) VALUES(1,$generation,$principal) ON CONFLICT(StateKey) DO NOTHING");

        Add(command, "$generation", Id(installationGeneration));

        Add(command, "$principal", Id(Guid.NewGuid()));

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        command.CommandText = "SELECT CASE WHEN typeof(NativeOperatorPrincipalId)='text' AND length(CAST(NativeOperatorPrincipalId AS BLOB))=36 THEN NativeOperatorPrincipalId ELSE '' END FROM guild_workspace_authority_state WHERE StateKey=1 AND InstallationGeneration=$generation";

        object? principal = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return principal is string text && TryGuid(text, out Guid parsed) ? parsed : Unavailable;
    }

    private static async Task<Result<GuildWorkspaceLeaseRecord>> UpdateWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildWorkspaceLeaseRecord expected, GuildWorkspaceLeaseRecord next, CancellationToken ct)
    {
        if (expected.Revision == long.MaxValue || !Valid(expected) || !Valid(next)) return Invalid;

        next = next with { Revision = expected.Revision + 1 };

        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE guild_workspace_leases SET HostInstanceId=$host,Epoch=$epoch,Revision=$revision,StateCode=$state,
                HeartbeatAtUtc=$heartbeat,ExpiresAtUtc=$expires,InFlightMutationCount=$count,ReasonCode=$reason
            WHERE Id=$id AND Epoch=$oldEpoch AND Revision=$oldRevision AND HostInstanceId=$oldHost
              AND OwnerKindCode=$kind AND PrincipalId=$principal AND QuestId IS $quest AND RootQuestId IS $root
              AND WorkspaceId=$workspace AND InstallationGeneration=$generation AND StateCode=$oldState
              AND SessionId IS $session AND CampaignId IS $campaign AND WorkspaceRoot=$workspaceRoot
              AND IdentityJson=$identity AND IdentityDigest=$identityDigest AND EffectKindCode=$effectKind
              AND IdempotencyKey=$key AND RequestDigest=$digest
              AND InFlightMutationCount=$oldCount AND HeartbeatAtUtc=$oldHeartbeat AND ExpiresAtUtc=$oldExpires
              AND ReasonCode IS $oldReason;
            """);

        Add(command, "$host", Id(next.HostInstanceId));

        Add(command, "$epoch", next.Epoch);

        Add(command, "$revision", next.Revision);
        Add(command, "$state", (int)next.State);

        Add(command, "$heartbeat", UtcInstantText.Format(next.HeartbeatAt));
        Add(command, "$expires", UtcInstantText.Format(next.ExpiresAt));

        Add(command, "$count", next.InFlightMutationCount);
        Add(command, "$reason", next.ReasonCode);

        Add(command, "$id", Id(expected.Id));

        Add(command, "$oldEpoch", expected.Epoch);
        Add(command, "$oldRevision", expected.Revision);

        Add(command, "$oldHost", Id(expected.HostInstanceId));
        Add(command, "$kind", (int)expected.Owner.Kind);

        Add(command, "$principal", Id(expected.Owner.PrincipalId));
        Add(command, "$quest", Id(expected.Owner.QuestId));

        Add(command, "$root", Id(expected.Owner.RootQuestId));
        Add(command, "$workspace", Id(expected.Owner.WorkspaceId));

        Add(command, "$generation", Id(expected.Owner.InstallationGeneration));
        Add(command, "$oldState", (int)expected.State);

        Add(command, "$oldCount", expected.InFlightMutationCount);

        Add(command, "$session", Id(expected.Owner.SessionId));

        Add(command, "$campaign", Id(expected.Owner.CampaignId));

        Add(command, "$workspaceRoot", expected.WorkspaceRoot);

        Add(command, "$identity", expected.IdentityJson);

        Add(command, "$identityDigest", expected.IdentityDigest);

        Add(command, "$effectKind", (int)expected.EffectKind);

        Add(command, "$key", Id(expected.IdempotencyKey));

        Add(command, "$digest", expected.RequestDigest);

        Add(command, "$oldHeartbeat", UtcInstantText.Format(expected.HeartbeatAt));

        Add(command, "$oldExpires", UtcInstantText.Format(expected.ExpiresAt));

        Add(command, "$oldReason", expected.ReasonCode);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? next : Conflict;
    }

    private static Result<GuildWorkspaceLeaseRecord> Decode(SqliteDataReader reader)
    {
        try
        {
            if (reader.GetInt64(23) != 1) return Unavailable;

            foreach (int ordinal in new[] { 0, 1, 2, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 21, 22 })
                if (reader.IsDBNull(ordinal)) return Unavailable;

            Guid Required(int ordinal) => ParseGuid(reader.GetString(ordinal));

            Guid? Optional(int ordinal) => reader.IsDBNull(ordinal) ? null : Required(ordinal);

            GuildWorkspaceLeaseRecord row = new(Required(0), new((GuildWorkspaceLeaseOwnerKind)reader.GetInt32(1), Required(2), Optional(3), Optional(4), Optional(5), Optional(6), Required(7), Required(8)),
                reader.GetString(9), reader.GetString(10), reader.GetString(11), Required(12), reader.GetInt64(13), reader.GetInt64(14),
                (GuildWorkspaceLeaseState)reader.GetInt32(15), UtcInstantText.Parse(reader.GetString(16)), UtcInstantText.Parse(reader.GetString(17)),
                reader.GetInt64(18), (GuildWorkspaceLeaseEffectKind)reader.GetInt32(19), reader.IsDBNull(20) ? null : reader.GetString(20), Required(21), reader.GetString(22));

            return Valid(row) ? row : Unavailable;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            return Unavailable;
        }
    }

    private static bool Valid(GuildWorkspaceLeaseRecord row) => row.Id != Guid.Empty && row.Owner.PrincipalId != Guid.Empty
        && row.Owner.WorkspaceId != Guid.Empty && row.Owner.InstallationGeneration != Guid.Empty && row.HostInstanceId != Guid.Empty
        && row.IdempotencyKey != Guid.Empty && row.Epoch >= 1 && row.Revision >= 1 && row.InFlightMutationCount >= 0
        && Enum.IsDefined(row.State) && Enum.IsDefined(row.EffectKind) && Enum.IsDefined(row.Owner.Kind)
        && (row.Owner.Kind == GuildWorkspaceLeaseOwnerKind.Operator ? row.Owner.QuestId is null && row.Owner.RootQuestId is null
            : row.Owner.QuestId is { } quest && quest != Guid.Empty && row.Owner.RootQuestId is { } root && root != Guid.Empty)
        && row.Owner.SessionId != Guid.Empty && row.Owner.CampaignId != Guid.Empty
        && (row.State != GuildWorkspaceLeaseState.Released || row.InFlightMutationCount == 0)
        && row.ExpiresAt > row.HeartbeatAt
        && row.WorkspaceRoot is { Length: > 0 } && Encoding.UTF8.GetByteCount(row.WorkspaceRoot) <= 32768
        && row.IdentityJson is { Length: > 0 } && Encoding.UTF8.GetByteCount(row.IdentityJson) <= MaximumIdentityBytes
        && row.ReasonCode is null or { Length: > 0 } && (row.ReasonCode is null || Encoding.UTF8.GetByteCount(row.ReasonCode) <= 128)
        && Digest(row.IdentityDigest) && Digest(row.RequestDigest);

    private static bool Digest(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool TryGuid(string value, out Guid parsed) => Guid.TryParseExact(value, "D", out parsed) && parsed != Guid.Empty && Id(parsed) == value;

    private static Guid ParseGuid(string value) => TryGuid(value, out Guid parsed) ? parsed : throw new FormatException("Invalid canonical workspace identity.");

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();

    private static string? Id(Guid? value) => value is { } id ? Id(id) : null;

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static Error Unavailable => new(ErrorCodes.Guild.Unavailable, "Guild workspace lease storage is unavailable.");

    private static Error Conflict => new(ErrorCodes.Guild.LeaseConflict, "The workspace lease changed or no longer admits this operation.");

    private static Error NotFound => new(ErrorCodes.Guild.NotFound, "The workspace lease is unavailable.");

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The workspace lease binding is invalid.");

    private static Error IdempotencyConflict => new(ErrorCodes.Guild.IdempotencyConflict, "The workspace lease request key already names a different intent.");
}
