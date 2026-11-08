using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildWorkspaceLeaseStore
{
    internal static async Task<Result<GuildWorkspaceLeaseMutationReceipt>> FindMutationReplayWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildWorkspaceLeaseOwner owner, Guid key, string digest, GuildAuthorityAction action,
        Guid leaseId, CancellationToken ct)
    {
        if (ActionCode(action) == 0 || key == Guid.Empty || leaseId == Guid.Empty || !Digest(digest)) return Invalid;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN typeof(Id)='text' AND length(CAST(Id AS BLOB))=36 THEN Id ELSE NULL END,
                CASE WHEN typeof(LeaseId)='text' AND length(CAST(LeaseId AS BLOB))=36 THEN LeaseId ELSE NULL END,
                CASE WHEN typeof(RequestDigest)='text' AND length(CAST(RequestDigest AS BLOB))=64 THEN RequestDigest ELSE NULL END,
                CASE WHEN typeof(ActionCode)='integer' THEN ActionCode ELSE NULL END,
                CASE WHEN typeof(AcceptedAtUtc)='text' AND length(CAST(AcceptedAtUtc AS BLOB))=28 THEN AcceptedAtUtc ELSE NULL END,
                CASE WHEN typeof(SnapshotJson)='text' AND length(CAST(SnapshotJson AS BLOB)) BETWEEN 1 AND 4096 THEN SnapshotJson ELSE NULL END
            FROM guild_workspace_lease_mutations WHERE PrincipalId=$principal AND QuestId IS $quest AND IdempotencyKey=$key;
            """);

        Add(command, "$principal", Id(owner.PrincipalId));

        Add(command, "$quest", Id(owner.QuestId));

        Add(command, "$key", Id(key));

        try
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                await reader.DisposeAsync().ConfigureAwait(false);

                if (action == GuildAuthorityAction.LeaseAcquire)
                {
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM guild_workspace_leases WHERE PrincipalId=$principal AND QuestId IS $quest AND IdempotencyKey=$key AND Id=$lease AND RequestDigest=$digest)";

                    Add(command, "$lease", Id(leaseId));

                    Add(command, "$digest", digest);

                    if (Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))) return NotFound;
                }

                command.CommandText = "SELECT EXISTS(SELECT 1 FROM guild_workspace_leases WHERE PrincipalId=$principal AND QuestId IS $quest AND IdempotencyKey=$key)";

                return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? IdempotencyConflict : NotFound;
            }

            for (int index = 0; index < 6; index++) if (reader.IsDBNull(index)) return Unavailable;

            Guid receiptId = ParseGuid(reader.GetString(0));

            Guid acceptedLease = ParseGuid(reader.GetString(1));

            if (acceptedLease != leaseId || reader.GetString(2) != digest || reader.GetInt64(3) != ActionCode(action)) return IdempotencyConflict;

            DateTimeOffset acceptedAt = UtcInstantText.Parse(reader.GetString(4));

            GuildWorkspaceLeaseMutationSnapshot? snapshot = JsonSerializer.Deserialize(reader.GetString(5), GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);

            if (snapshot is null || !ValidSnapshot(snapshot)
                || action == GuildAuthorityAction.LeaseAcquire && !IsAcquisitionSnapshot(snapshot, acceptedAt)) return Unavailable;

            await reader.DisposeAsync().ConfigureAwait(false);

            Result<GuildWorkspaceLeaseRecord> current = await ReadWithinAsync(connection, transaction, leaseId, ct).ConfigureAwait(false);

            if (current.IsFailure || current.Value.Owner != owner || snapshot.Epoch > current.Value.Epoch || snapshot.Revision > current.Value.Revision) return Unavailable;

            GuildWorkspaceLeaseRecord original = snapshot.Apply(current.Value);

            return Valid(original) ? new GuildWorkspaceLeaseMutationReceipt(receiptId, key, digest, action, acceptedAt, original) : Unavailable;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            return Unavailable;
        }
    }

    internal static async Task<Result<GuildWorkspaceLeaseMutationReceipt>> RecordMutationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildWorkspaceLeaseRecord row, GuildMutationIdentity mutation, GuildAuthorityAction action,
        DateTimeOffset acceptedAt, CancellationToken ct)
    {
        if (!Valid(row) || mutation.IdempotencyKey == Guid.Empty || !Digest(mutation.CanonicalRequestDigest) || ActionCode(action) == 0) return Invalid;

        if (action == GuildAuthorityAction.LeaseAcquire && (row.IdempotencyKey != mutation.IdempotencyKey
            || row.RequestDigest != mutation.CanonicalRequestDigest || row.Epoch != 1 || row.Revision != 1
            || row.State != GuildWorkspaceLeaseState.Held || row.InFlightMutationCount != 0 || acceptedAt != row.HeartbeatAt)) return Invalid;

        var existing = await FindMutationReplayWithinAsync(connection, transaction, row.Owner, mutation.IdempotencyKey,
            mutation.CanonicalRequestDigest, action, row.Id, ct).ConfigureAwait(false);

        if (existing.IsSuccess) return existing.Value.Result == row ? existing : Conflict;

        if (existing.Error.Code != ErrorCodes.Guild.NotFound) return existing.Error;

        Result<GuildWorkspaceLeaseRecord> current = await ReadWithinAsync(connection, transaction, row.Id, ct).ConfigureAwait(false);

        if (current.IsFailure || current.Value != row) return Conflict;

        string snapshot = JsonSerializer.Serialize(GuildWorkspaceLeaseMutationSnapshot.From(row), GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);

        if (Encoding.UTF8.GetByteCount(snapshot) > 4096) return Invalid;

        GuildWorkspaceLeaseMutationReceipt receipt = new(Guid.NewGuid(), mutation.IdempotencyKey, mutation.CanonicalRequestDigest, action, acceptedAt, row);

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_workspace_lease_mutations(Id,PrincipalId,QuestId,IdempotencyKey,RequestDigest,ActionCode,LeaseId,AcceptedAtUtc,SnapshotJson)
            VALUES($id,$principal,$quest,$key,$digest,$action,$lease,$accepted,$snapshot);
            """);

        Add(command, "$id", Id(receipt.Id));

        Add(command, "$principal", Id(row.Owner.PrincipalId));

        Add(command, "$quest", Id(row.Owner.QuestId));

        Add(command, "$key", Id(mutation.IdempotencyKey));

        Add(command, "$digest", mutation.CanonicalRequestDigest);

        Add(command, "$action", ActionCode(action));

        Add(command, "$lease", Id(row.Id));

        Add(command, "$accepted", UtcInstantText.Format(acceptedAt));

        Add(command, "$snapshot", snapshot);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return receipt;
    }

    private static async Task<bool> MutationKeyExistsWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildWorkspaceLeaseOwner owner, Guid key, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT EXISTS(SELECT 1 FROM guild_workspace_lease_mutations WHERE PrincipalId=$principal AND QuestId IS $quest AND IdempotencyKey=$key)");

        Add(command, "$principal", Id(owner.PrincipalId));

        Add(command, "$quest", Id(owner.QuestId));

        Add(command, "$key", Id(key));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    private static int ActionCode(GuildAuthorityAction action) => action switch
    {
        GuildAuthorityAction.LeaseRenew => 1,

        GuildAuthorityAction.LeaseRelease => 2,

        GuildAuthorityAction.LeaseAcquire => 3,

        _ => 0,
    };

    internal static async Task<Result> ValidateMutationReceiptsWithinAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        // Verify historical receipts without re-materializing their up-to-1-MiB immutable scope
        // evidence for every heartbeat. Only the small bounded mutable snapshot is decoded here.
        string identities = string.Join(" AND ", new[] { "m.Id", "m.PrincipalId", "m.IdempotencyKey", "m.LeaseId" }.Select(GuidSql));

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT CASE WHEN typeof(m.SnapshotJson)='text' AND length(CAST(m.SnapshotJson AS BLOB)) BETWEEN 1 AND 4096 THEN m.SnapshotJson ELSE NULL END,
                CASE WHEN typeof(l.Epoch)='integer' THEN l.Epoch ELSE NULL END,
                CASE WHEN typeof(l.Revision)='integer' THEN l.Revision ELSE NULL END,
                CASE WHEN typeof(m.AcceptedAtUtc)='text' AND length(CAST(m.AcceptedAtUtc AS BLOB))=28 THEN m.AcceptedAtUtc ELSE NULL END,
                CASE WHEN {identities} AND (m.QuestId IS NULL OR ({GuidSql("m.QuestId")}))
                    AND m.PrincipalId=l.PrincipalId AND m.QuestId IS l.QuestId
                    AND typeof(m.RequestDigest)='text' AND length(CAST(m.RequestDigest AS BLOB))=64 AND m.RequestDigest NOT GLOB '*[^0-9A-F]*'
                    AND typeof(m.ActionCode)='integer' AND m.ActionCode IN(1,2,3)
                    AND (m.ActionCode<>3 OR (m.IdempotencyKey=l.IdempotencyKey AND m.RequestDigest=l.RequestDigest)) THEN 1 ELSE 0 END,
                CASE WHEN typeof(m.ActionCode)='integer' THEN m.ActionCode ELSE 0 END
            FROM guild_workspace_lease_mutations m LEFT JOIN guild_workspace_leases l ON l.Id=m.LeaseId;
            """);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        try
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (Enumerable.Range(0, 4).Any(reader.IsDBNull) || reader.GetInt64(4) != 1) return Result.Failure(Unavailable);

                GuildWorkspaceLeaseMutationSnapshot? snapshot = JsonSerializer.Deserialize(reader.GetString(0), GuildWorkspaceLeaseMutationJsonContext.Default.GuildWorkspaceLeaseMutationSnapshot);

                if (snapshot is null || !ValidSnapshot(snapshot) || snapshot.Epoch > reader.GetInt64(1) || snapshot.Revision > reader.GetInt64(2)) return Result.Failure(Unavailable);

                DateTimeOffset acceptedAt = UtcInstantText.Parse(reader.GetString(3));

                if (reader.GetInt64(5) == 3 && !IsAcquisitionSnapshot(snapshot, acceptedAt)) return Result.Failure(Unavailable);
            }

            return Result.Success();
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or InvalidCastException or ArgumentException)
        {
            return Result.Failure(Unavailable);
        }
    }

    private static bool IsAcquisitionSnapshot(GuildWorkspaceLeaseMutationSnapshot snapshot, DateTimeOffset acceptedAt) =>
        snapshot.Epoch == 1 && snapshot.Revision == 1 && snapshot.State == GuildWorkspaceLeaseState.Held
        && snapshot.InFlightMutationCount == 0 && snapshot.HeartbeatAt == acceptedAt;

    private static bool ValidSnapshot(GuildWorkspaceLeaseMutationSnapshot snapshot) => snapshot.HostInstanceId != Guid.Empty
        && snapshot.Epoch >= 1 && snapshot.Revision >= 1 && snapshot.InFlightMutationCount >= 0 && Enum.IsDefined(snapshot.State)
        && (snapshot.State != GuildWorkspaceLeaseState.Released || snapshot.InFlightMutationCount == 0)
        && snapshot.ExpiresAt > snapshot.HeartbeatAt
        && (snapshot.ReasonCode is null || snapshot.ReasonCode.Length > 0 && Encoding.UTF8.GetByteCount(snapshot.ReasonCode) <= 128);

    private static string GuidSql(string column) => $"typeof({column})='text' AND {column}=upper({column}) AND length(CAST({column} AS BLOB))=36"
        + $" AND substr({column},9,1)='-' AND substr({column},14,1)='-' AND substr({column},19,1)='-' AND substr({column},24,1)='-'"
        + $" AND length(replace({column},'-',''))=32 AND replace({column},'-','') NOT GLOB '*[^0-9A-F]*' AND {column}<>'00000000-0000-0000-0000-000000000000'";
}

internal sealed record GuildWorkspaceLeaseMutationSnapshot(
    [property: JsonRequired] Guid HostInstanceId,
    [property: JsonRequired] long Epoch,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] GuildWorkspaceLeaseState State,
    [property: JsonRequired] DateTimeOffset HeartbeatAt,
    [property: JsonRequired] DateTimeOffset ExpiresAt,
    [property: JsonRequired] long InFlightMutationCount,
    [property: JsonRequired] string? ReasonCode)
{
    internal static GuildWorkspaceLeaseMutationSnapshot From(GuildWorkspaceLeaseRecord row) => new(row.HostInstanceId,
        row.Epoch, row.Revision, row.State, row.HeartbeatAt, row.ExpiresAt, row.InFlightMutationCount, row.ReasonCode);

    internal GuildWorkspaceLeaseRecord Apply(GuildWorkspaceLeaseRecord row) => row with
    {
        HostInstanceId = HostInstanceId,
        Epoch = Epoch,
        Revision = Revision,
        State = State,
        HeartbeatAt = HeartbeatAt,
        ExpiresAt = ExpiresAt,
        InFlightMutationCount = InFlightMutationCount,
        ReasonCode = ReasonCode,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildWorkspaceLeaseMutationSnapshot))]
internal sealed partial class GuildWorkspaceLeaseMutationJsonContext : JsonSerializerContext;
