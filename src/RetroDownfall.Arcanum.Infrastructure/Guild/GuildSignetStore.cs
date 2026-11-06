using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Signet SQL participant. Callers hold installation admission, then the exact root gate.</summary>
internal sealed class GuildSignetStore(IGrimoireOrdinaryConnectionFactory connections)
{
    private const string Columns = "SignetId,AgentId,RunId,QuestId,RootQuestId,IssuerSignetId,GrantJson,IssuedAtUtc,OriginPrincipalId,SessionId,CampaignId,WorkspaceId,PeerId,PeerGrantRevision,InstallationGeneration,AuthorityEpoch,Revision,CredentialGeneration,TokenDigest,RevocationGeneration,Revoked,RestoredInert,Retired,CancellationRequested,WorkspaceIdentityPolicyVersion,WorkspaceIdentityRevision,WorkspaceRootIdentity";

    private static readonly string ReadColumns = Columns.Replace("GrantJson",
        "CASE WHEN length(CAST(GrantJson AS BLOB))<=$maximumGrantBytes THEN GrantJson ELSE NULL END", StringComparison.Ordinal);

    private const string VariableTextBytes = "length(CAST(GrantJson AS BLOB))+COALESCE(length(CAST(PeerId AS BLOB)),0)+COALESCE(length(CAST(PeerGrantRevision AS BLOB)),0)";

    private static readonly string ReadShape = string.Join(" AND ",
        new[] { "SignetId", "AgentId", "RunId", "QuestId", "RootQuestId", "OriginPrincipalId", "InstallationGeneration", "AuthorityEpoch" }
            .Select(column => $"(typeof({column})='text' AND length(CAST({column} AS BLOB))=36)")
            .Concat(new[] { "IssuerSignetId", "SessionId", "CampaignId", "WorkspaceId" }
                .Select(column => $"({column} IS NULL OR (typeof({column})='text' AND length(CAST({column} AS BLOB))=36))")))
        + " AND typeof(GrantJson)='text' AND typeof(IssuedAtUtc)='text' AND length(CAST(IssuedAtUtc AS BLOB))=28"
        + " AND (PeerId IS NULL OR typeof(PeerId)='text') AND (PeerGrantRevision IS NULL OR typeof(PeerGrantRevision)='text')"
        + " AND ((PeerId IS NULL)=(PeerGrantRevision IS NULL))"
        + " AND typeof(TokenDigest)='blob' AND length(TokenDigest)=32"
        + " AND typeof(Revision)='integer' AND Revision>=1 AND typeof(CredentialGeneration)='integer' AND CredentialGeneration>=1"
        + " AND typeof(RevocationGeneration)='integer' AND RevocationGeneration>=0"
        + " AND typeof(Revoked)='integer' AND Revoked IN(0,1) AND typeof(RestoredInert)='integer' AND RestoredInert IN(0,1)"
        + " AND typeof(Retired)='integer' AND Retired IN(0,1) AND typeof(CancellationRequested)='integer' AND CancellationRequested IN(0,1)"
        + " AND ((WorkspaceId IS NULL AND WorkspaceIdentityPolicyVersion IS NULL AND WorkspaceIdentityRevision IS NULL AND WorkspaceRootIdentity IS NULL)"
        + " OR (WorkspaceId IS NOT NULL AND typeof(WorkspaceIdentityPolicyVersion)='integer' AND WorkspaceIdentityPolicyVersion BETWEEN 1 AND 4294967295"
        + " AND typeof(WorkspaceIdentityRevision)='integer' AND WorkspaceIdentityRevision>=1"
        + " AND typeof(WorkspaceRootIdentity)='blob' AND length(WorkspaceRootIdentity)=32))";

    internal async Task<Result<T>> ExecuteAsync<T>(bool write,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        try
        {
            Result<IGrimoireOrdinaryConnectionLease> acquired = await connections.OpenFreshAsync(write
                ? GrimoireOrdinaryFreshConnectionKind.ReadWrite : GrimoireOrdinaryFreshConnectionKind.ReadOnly, ct).ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return acquired.Error;
            }

            await using IGrimoireOrdinaryConnectionLease lease = acquired.Value;

            await using SqliteTransaction transaction = lease.Connection.BeginTransaction(deferred: !write);

            Result available = await GuildStorageAvailability.CheckAsync(lease.Connection, transaction, ct).ConfigureAwait(false);

            if (available.IsFailure)
            {
                return available.Error;
            }

            Result<T> result = await action(lease.Connection, transaction, ct).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                ct.ThrowIfCancellationRequested();

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }

            return result;
        }
        catch (SqliteException exception)
        {
            return exception.SqliteErrorCode switch
            {
                5 or 6 or 13 => new Error(ErrorCodes.Guild.StoragePressure, "Guild authority storage could not admit this operation."),
                19 => Conflict,
                _ => Unavailable,
            };
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException or InvalidCastException)
        {
            return Unavailable;
        }
    }

    internal static async Task<Result<Guid>> EpochAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT CASE WHEN typeof(Epoch)='text' AND length(CAST(Epoch AS BLOB))=36 THEN Epoch ELSE '' END FROM guild_signet_authority_state WHERE StateKey=1");

        object? value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        return value is string text && Guid.TryParseExact(text, "D", out Guid epoch) && epoch != Guid.Empty
            && text == epoch.ToString("D").ToUpperInvariant()
            ? Result<Guid>.Success(epoch) : Result<Guid>.Failure(Unavailable);
    }

    internal static async Task<Result<GuildSignetRecord>> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid id, bool byQuest, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            $"SELECT {ReadColumns},{VariableTextBytes},CASE WHEN {ReadShape} THEN 1 ELSE 0 END FROM guild_signets WHERE {(byQuest ? "QuestId" : "SignetId")}=$id");

        Add(command, "$id", Id(id));

        Add(command, "$maximumGrantBytes", GuildSignetGrantJson.MaximumBytes);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return NotFound;
        }

        // Inspect SQLite byte counts and fixed identity shapes before any retained text getter.
        if (reader.IsDBNull(6) || reader.GetInt64(27) > GuildSignetGrantJson.MaximumBytes)
        {
            return GuildSignetGrantJson.TooLarge;
        }

        if (reader.GetInt64(28) != 1)
        {
            return Unavailable;
        }

        string serialized = reader.GetString(6);

        GuildGrant? grant = JsonSerializer.Deserialize(serialized, GuildJsonContext.Default.GuildGrant);

        if (grant is null || GuildGrantPolicy.Validate(grant).IsFailure)
        {
            return Unavailable;
        }

        Result<string> canonical = GuildSignetGrantJson.Encode(grant);

        if (canonical.IsFailure || canonical.Value != serialized)
        {
            return canonical.IsFailure ? canonical.Error : Unavailable;
        }

        string issuedText = reader.GetString(7);

        DateTimeOffset issued = UtcInstantText.Parse(issuedText);

        if (UtcInstantText.Format(issued) != issuedText)
        {
            return Unavailable;
        }

        GuildSignetBinding binding = new(ParseId(reader, 0), ParseId(reader, 1), ParseId(reader, 2),
            ParseId(reader, 3), ParseId(reader, 4), NullableId(reader, 5), grant, issued, ParseId(reader, 8));

        GuildSignetOwnerBinding owners = new(NullableId(reader, 9), NullableId(reader, 10), NullableId(reader, 11),
            Text(reader, 12), Text(reader, 13), reader.IsDBNull(24) ? null : checked((uint)reader.GetInt64(24)),
            reader.IsDBNull(25) ? null : reader.GetInt64(25), reader.IsDBNull(26) ? null : (byte[])reader.GetValue(26));

        return new GuildSignetRecord(binding, owners, ParseId(reader, 14), ParseId(reader, 15),
            reader.GetInt64(16), reader.GetInt64(17), (byte[])reader.GetValue(18), reader.GetInt64(19),
            reader.GetBoolean(20), reader.GetBoolean(21), reader.GetBoolean(22), reader.GetBoolean(23));
    }

    internal static async Task<Result<ImmutableArray<GuildSignetRecord>>> LineageAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildSignetRecord target, CancellationToken ct)
    {
        ImmutableArray<GuildSignetRecord>.Builder lineage = ImmutableArray.CreateBuilder<GuildSignetRecord>();

        HashSet<Guid> visited = [];

        HashSet<Guid> agents = [];

        GuildSignetRecord current = target;

        while (true)
        {
            if (!visited.Add(current.Binding.SignetId) || !agents.Add(current.Binding.AgentId)
                || current.Binding.RootQuestId != target.Binding.RootQuestId)
            {
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "The persisted Guild authority lineage is invalid.");
            }

            lineage.Add(current);

            if (current.Binding.IssuerSignetId is not { } issuer)
            {
                return current.Binding.QuestId == current.Binding.RootQuestId
                    ? Result<ImmutableArray<GuildSignetRecord>>.Success(lineage.ToImmutable())
                    : Result<ImmutableArray<GuildSignetRecord>>.Failure(Unavailable);
            }

            Result<GuildSignetRecord> parent = await ReadAsync(connection, transaction, issuer, false, ct).ConfigureAwait(false);

            if (parent.IsFailure)
            {
                return new Error(ErrorCodes.Guild.AuthorityRevoked, "The Guild authority lineage is unavailable.");
            }

            if (parent.Value.Owners.CampaignId != current.Owners.CampaignId
                || parent.Value.Owners.WorkspaceId != current.Owners.WorkspaceId
                || parent.Value.Binding.OriginPrincipalId != current.Binding.OriginPrincipalId
                || parent.Value.Owners.PeerId != current.Owners.PeerId
                || parent.Value.Owners.PeerGrantRevision != current.Owners.PeerGrantRevision)
            {
                return new Error(ErrorCodes.Guild.AuthorityMismatch, "The immutable Guild owner lineage is inconsistent.");
            }

            current = parent.Value;
        }
    }

    internal static async Task<Result> OwnersAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSignetRecord row, CancellationToken ct)
    {
        GuildSignetOwnerBinding owners = row.Owners;

        Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, owners.SessionId, owners.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure)
        {
            return owner;
        }

        Result workspace = await GuildStore.RequireOwnerWithinAsync(connection, transaction, null, owners.WorkspaceId, ct).ConfigureAwait(false);

        if (workspace.IsFailure)
        {
            return workspace;
        }

        if (row.Binding.Grant.Workspaces.Any(scope => scope.WorkspaceId != owners.WorkspaceId)
            || row.Binding.Grant.Memory.Any(scope => scope.Scope.Kind == GuildMemoryScopeKind.Campaign && scope.Scope.Id != owners.CampaignId)
            || (owners.WorkspaceId is null
                ? owners.WorkspaceIdentityPolicyVersion is not null || owners.WorkspaceIdentityRevision is not null || owners.WorkspaceRootIdentity is not null
                : owners.WorkspaceIdentityPolicyVersion is null || owners.WorkspaceIdentityRevision is null || owners.WorkspaceRootIdentity is not { Length: 32 }))
        {
            return new Error(ErrorCodes.Guild.AuthorityMismatch, "The grant does not match its canonical owner binding.");
        }

        foreach (GuildMemoryGrant scope in row.Binding.Grant.Memory.Where(scope => scope.Scope.Kind == GuildMemoryScopeKind.Session))
        {
            Result session = await GuildStore.RequireOwnerWithinAsync(connection, transaction, scope.Scope.Id, owners.CampaignId, ct).ConfigureAwait(false);

            if (session.IsFailure)
            {
                return session;
            }
        }

        return Result.Success();
    }

    internal static async Task<Result<bool>> InsertAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSignetRecord row, CancellationToken ct)
    {
        Result<string> serialized = GuildSignetGrantJson.Encode(row.Binding.Grant);

        if (serialized.IsFailure)
        {
            return serialized.Error;
        }

        Result bounded = GuildSignetGrantJson.RequireEnvelope(serialized.Value, row.Owners);

        if (bounded.IsFailure)
        {
            return bounded.Error;
        }

        await using SqliteCommand command = Command(connection, transaction, $"""
            INSERT INTO guild_signets({Columns}) VALUES
            ($id,$agent,$run,$quest,$root,$issuer,$grant,$issued,$origin,$session,$campaign,$workspace,$peer,$peerRevision,
             $installation,$epoch,$revision,$credential,$digest,$revocation,$revoked,$inert,$retired,$cancel,$workspacePolicy,$workspaceRevision,$workspaceIdentity)
            """);

        GuildSignetBinding binding = row.Binding;

        Add(command, "$id", Id(binding.SignetId));

        Add(command, "$agent", Id(binding.AgentId));

        Add(command, "$run", Id(binding.RunId));

        Add(command, "$quest", Id(binding.QuestId));

        Add(command, "$root", Id(binding.RootQuestId));

        Add(command, "$issuer", Id(binding.IssuerSignetId));

        Add(command, "$grant", serialized.Value);

        Add(command, "$issued", UtcInstantText.Format(binding.IssuedAtUtc));

        Add(command, "$origin", Id(binding.OriginPrincipalId));

        Add(command, "$session", Id(row.Owners.SessionId));

        Add(command, "$campaign", Id(row.Owners.CampaignId));

        Add(command, "$workspace", Id(row.Owners.WorkspaceId));

        Add(command, "$peer", row.Owners.PeerId);

        Add(command, "$peerRevision", row.Owners.PeerGrantRevision);

        Add(command, "$installation", Id(row.InstallationGeneration));

        Add(command, "$epoch", Id(row.AuthorityEpoch));

        Add(command, "$revision", row.Revision);

        Add(command, "$credential", row.CredentialGeneration);

        Add(command, "$digest", row.TokenDigest);

        Add(command, "$revocation", row.RevocationGeneration);

        Add(command, "$revoked", row.Revoked ? 1 : 0);

        Add(command, "$inert", row.RestoredInert ? 1 : 0);

        Add(command, "$retired", row.Retired ? 1 : 0);

        Add(command, "$cancel", row.CancellationRequested ? 1 : 0);

        Add(command, "$workspacePolicy", row.Owners.WorkspaceIdentityPolicyVersion);

        Add(command, "$workspaceRevision", row.Owners.WorkspaceIdentityRevision);

        Add(command, "$workspaceIdentity", row.Owners.WorkspaceRootIdentity);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result<bool>.Success(true) : Result<bool>.Failure(Conflict);
    }

    internal static async Task<Result<bool>> RotateAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSignetRecord row, byte[] digest, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE guild_signets SET TokenDigest=$digest,CredentialGeneration=CredentialGeneration+1,Revision=Revision+1
            WHERE SignetId=$id AND Revision=$revision AND CredentialGeneration=$credential AND Revoked=0 AND RestoredInert=0 AND Retired=0
            """);

        Add(command, "$digest", digest);

        Add(command, "$id", Id(row.Binding.SignetId));

        Add(command, "$revision", row.Revision);

        Add(command, "$credential", row.CredentialGeneration);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result<bool>.Success(true) : Result<bool>.Failure(Conflict);
    }

    internal static async Task<Result<SignetRevocationDto>> RevokeAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSignetRecord row, GuildMutationIdentity mutation, GuildRevokeSignetRequest request, CancellationToken ct)
    {
        await using (SqliteCommand replay = Command(connection, transaction, """
            SELECT CanonicalRequestDigest,ExpectedRevision,ReasonCode,Revision,Generation,
              CASE WHEN typeof(CanonicalRequestDigest)='text' AND length(CAST(CanonicalRequestDigest AS BLOB))=64
                AND CanonicalRequestDigest NOT GLOB '*[^0-9A-F]*'
                AND typeof(ExpectedRevision)='integer' AND ExpectedRevision>=1
                AND typeof(Revision)='integer' AND Revision>=1 AND typeof(Generation)='integer' AND Generation>=1
                AND ReasonCode IN('operatorRequested','ownerDeleted') THEN 1 ELSE 0 END
            FROM guild_signet_revocations
            WHERE SignetId=$id AND IdempotencyKey=$key
            """))
        {
            Add(replay, "$id", Id(row.Binding.SignetId));

            Add(replay, "$key", Id(request.IdempotencyKey));

            await using SqliteDataReader reader = await replay.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetInt64(5) != 1)
                {
                    return Unavailable;
                }

                return reader.GetString(0) == mutation.CanonicalRequestDigest && reader.GetInt64(1) == request.ExpectedRevision
                    && reader.GetString(2) == Reason(request.ReasonCode)
                    ? Result<SignetRevocationDto>.Success(Revocation(row.Binding.SignetId, reader.GetInt64(3), reader.GetInt64(4)))
                    : Result<SignetRevocationDto>.Failure(new(ErrorCodes.Guild.IdempotencyConflict, "The Guild mutation key already binds a different request."));
            }
        }

        if (row.Revision != request.ExpectedRevision)
        {
            return Conflict;
        }

        long revision = checked(row.Revision + (row.Revoked ? 0 : 1));

        long generation = checked(row.RevocationGeneration + (row.Revoked ? 0 : 1));

        if (!row.Revoked)
        {
            await using SqliteCommand update = Command(connection, transaction, """
                UPDATE guild_signets SET Revoked=1,CancellationRequested=1,RevocationGeneration=$generation,Revision=$next
                WHERE SignetId=$id AND Revision=$revision
                """);

            Add(update, "$id", Id(row.Binding.SignetId));

            Add(update, "$revision", row.Revision);

            Add(update, "$next", revision);

            Add(update, "$generation", generation);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return Conflict;
            }
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_signet_revocations(SignetId,IdempotencyKey,CanonicalRequestDigest,ExpectedRevision,ReasonCode,Revision,Generation)
            VALUES($id,$key,$digest,$expected,$reason,$revision,$generation)
            """);

        Add(insert, "$id", Id(row.Binding.SignetId));

        Add(insert, "$key", Id(request.IdempotencyKey));

        Add(insert, "$digest", mutation.CanonicalRequestDigest);

        Add(insert, "$expected", request.ExpectedRevision);

        Add(insert, "$reason", Reason(request.ReasonCode));

        Add(insert, "$revision", revision);

        Add(insert, "$generation", generation);

        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return Revocation(row.Binding.SignetId, revision, generation);
    }

    private static SignetRevocationDto Revocation(Guid id, long revision, long generation) => new(id, revision, generation, true, true, false, 0);

    private static string Reason(GuildCancellationReason reason) => reason switch
    {
        GuildCancellationReason.OperatorRequested => "operatorRequested",
        GuildCancellationReason.OwnerDeleted => "ownerDeleted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Id(Guid value) => value.ToString("D").ToUpperInvariant();

    private static string? Id(Guid? value) => value is { } id ? Id(id) : null;

    private static Guid ParseId(SqliteDataReader reader, int ordinal)
    {
        string value = reader.GetString(ordinal);

        return Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty && value == Id(id)
            ? id : throw new FormatException("The stored Guild identity is not canonical.");
    }

    private static Guid? NullableId(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseId(reader, ordinal);

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static Error Conflict => new(ErrorCodes.Guild.RevisionConflict, "The Guild authority revision changed.");

    internal static Error NotFound => new(ErrorCodes.Guild.NotFound, "The Guild authority is unavailable.");

    internal static Error Unavailable => new(ErrorCodes.Guild.AuthorityUnavailable, "The Guild authority storage or lineage is unavailable.");
}
