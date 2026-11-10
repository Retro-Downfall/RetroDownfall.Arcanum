using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.A2A;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildRemoteExecutionInput(int Version, string DiscoveryEndpoint, GuildRemoteTransportDescriptor Transport,
    A2ADispatchMode Mode, ImmutableArray<string> AcceptedOutputModes, string? SkillId, ImmutableArray<string> DelegationChain);

internal sealed record GuildRemoteExecutionBinding(Guid QuestId, GuildDataRef Input);

internal sealed record GuildRemoteRecoveryTarget(string DiscoveryEndpoint, GuildRemoteTransportDescriptor Transport);

internal static class GuildRemoteExecutionStore
{
    internal static async Task<Result<GuildRemoteTransportDescriptor?>> ResolveTransportWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid agentId, GuildCardReadScope scope, CancellationToken ct)
    {
        var selected = await GuildStore.SelectQuestCardWithinAsync(connection, transaction, agentId, scope, ct).ConfigureAwait(false);

        if (selected.IsFailure) return selected.Error;

        var card = await GuildStore.ResolveQuestCardWithinAsync(connection, transaction, agentId,
            selected.Value.CardId, selected.Value.Revision, scope, ct).ConfigureAwait(false);

        if (card.IsFailure) return card.Error;

        if (card.Value.Payload.Card.Source.Kind != GuildAgentSourceKind.Remote)
            return Result<GuildRemoteTransportDescriptor?>.Success(null);

        GuildRemoteTransportDescriptor? transport = card.Value.Payload.RemoteTransport;

        return transport is not null && card.Value.Payload.Card.Source.PeerId == transport.PeerId
            && ValidTransport(transport) && ValidUrl(card.Value.Payload.DiscoveryEndpoint)
            ? Result<GuildRemoteTransportDescriptor?>.Success(transport) : Missing;
    }

    internal static async Task<Result<GuildRemoteExecutionBinding>> CreateWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored quest, GuildRemoteExecutionInput input,
        ArtifactSensitivityLabel? sourceLabel, DateTimeOffset now, CancellationToken ct)
    {
        if (quest.Quest.ResolvedGrant.Budget.Usd.Mode != GuildBudgetMode.Unlimited
            || quest.Quest.ResolvedGrant.Budget.Tokens.Mode != GuildBudgetMode.Unlimited)
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General remote transport has no qualified finite spend contract.");

        if (!Valid(input) || now == default) return Missing;

        byte[] body = JsonSerializer.SerializeToUtf8Bytes(input, GuildStorageJsonContext.Default.GuildRemoteExecutionInput);

        if (body.Length > GuildStore.MaximumContentBytes)
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The remote execution input exceeds the content representation bound.");

        byte[] digest = SHA256.HashData(body);

        // Recovery retains only configured transport metadata, never a goal, answer or credential value.
        GuildRemoteTransportDescriptor recoveryTransport = input.Transport with
        {
            CredentialReference = null,
            ProviderLink = null,
            DocumentationLink = null,
        };

        byte[] recovery = JsonSerializer.SerializeToUtf8Bytes(recoveryTransport, GuildJsonContext.Default.GuildRemoteTransportDescriptor);

        byte[] discovery = System.Text.Encoding.UTF8.GetBytes(input.DiscoveryEndpoint);

        if ((long)recovery.Length + discovery.Length > GuildStore.MaximumContentBytes)
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The retained remote transport metadata exceeds its representation bound.");

        byte[] recoveryDigest = RecoveryDigest(discovery, recovery);

        await using (SqliteCommand prior = Command(connection, transaction, "SELECT InputContentId,InputDigest,InputByteLength FROM guild_remote_executions WHERE QuestId=$quest", quest.Quest.Id))
        {
            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                return ((byte[])reader.GetValue(1)).AsSpan().SequenceEqual(digest)
                    ? new GuildRemoteExecutionBinding(quest.Quest.Id, new(Guid.Parse(reader.GetString(0)), GuildContentKind.ExecutionInput,
                        1, Convert.ToHexString(digest), "application/json", reader.GetInt64(2), false))
                    : new Error(ErrorCodes.Guild.IdempotencyConflict, "The accepted remote destination or options differ.");
        }

        await using (SqliteCommand live = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_quests q WHERE q.Id=$quest AND q.Retired=0 AND q.CancellationRequested=0
                AND NOT EXISTS(SELECT 1 FROM guild_dispatches d WHERE d.QuestId=q.Id)
                AND NOT EXISTS(SELECT 1 FROM guild_local_executions l WHERE l.QuestId=q.Id))
            """, quest.Quest.Id))
        {
            if (!Equals(1L, await live.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Missing;
        }

        var label = await GuildLocalExecutionStore.ValidateReferenceAsync(connection, transaction, quest.Quest.GoalRef, ct).ConfigureAwait(false);

        if (label.IsFailure) return label.Error;

        ArtifactSensitivityLabel? inherited = sourceLabel ?? label.Value;

        Guid contentId = Guid.NewGuid();

        DerivedArtifactWrite artifact = new(SensitiveArtifactKind.GuildContent, contentId, quest.Quest.SessionId,
            quest.Quest.CampaignId, inherited?.TurnId, 1, new CovenantDigest(digest), inherited?.Sensitivity ?? ContentSensitivity.None,
            inherited?.Provenance ?? GenerationProvenance.CreateExact([]), inherited?.ProducingPlanDigest,
            inherited?.ProducingAdmissionDigest, inherited?.ProducingMaintenanceReceiptDigest);

        var content = await GuildStore.WriteContentWithinAsync(connection, transaction,
            new(contentId, quest.Quest.Id, GuildContentKind.ExecutionInput, 1, quest.Quest.SessionId,
                quest.Quest.CampaignId, "application/json", body, artifact), now, ct).ConfigureAwait(false);

        if (content.IsFailure) return content.Error;

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_remote_executions(QuestId,InputContentId,InputDigest,InputByteLength,
                RecoveryDiscoveryEndpoint,RecoveryTransport,RecoveryDigest)
            VALUES($quest,$content,$digest,$bytes,$discovery,$recovery,$recoveryDigest)
            """, quest.Quest.Id);

        insert.Parameters.AddWithValue("$content", Id(contentId));
        insert.Parameters.AddWithValue("$digest", digest);
        insert.Parameters.AddWithValue("$bytes", body.Length);
        insert.Parameters.AddWithValue("$discovery", input.DiscoveryEndpoint);
        insert.Parameters.AddWithValue("$recovery", recovery);
        insert.Parameters.AddWithValue("$recoveryDigest", recoveryDigest);

        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return new GuildRemoteExecutionBinding(quest.Quest.Id, content.Value);
    }

    internal static async Task<Result<GuildRemoteExecutionInput>> ReadInputWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using (SqliteCommand erased = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_remote_executions r JOIN guild_content_erasure_receipts e ON e.ContentId=r.InputContentId
                WHERE r.QuestId=$quest)
            """, questId))
        {
            if (Equals(1L, await erased.ExecuteScalarAsync(ct).ConfigureAwait(false)))
                return new Error(ErrorCodes.Guild.ContentErased, "The accepted remote execution input was erased.");
        }

        await using SqliteCommand query = Command(connection, transaction, """
            SELECT r.InputContentId,r.InputDigest,r.InputByteLength,c.Body,c.SensitivityCode,
                EXISTS(SELECT 1 FROM guild_content_erasure_receipts e WHERE e.ContentId=r.InputContentId)
            FROM guild_remote_executions r JOIN guild_content c ON c.ContentId=r.InputContentId
            JOIN guild_quests q ON q.Id=r.QuestId
            WHERE r.QuestId=$quest AND c.ContentKindCode=9 AND c.SourceRecordId=q.Id AND c.Revision=1
                AND c.SessionId IS q.SessionId AND c.CampaignId IS q.CampaignId
                AND c.Digest=r.InputDigest AND length(c.Body)=r.InputByteLength AND length(c.Body)<=65536
            """, questId);

        await using SqliteDataReader reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Missing;

        if (reader.GetBoolean(5)) return new Error(ErrorCodes.Guild.ContentErased, "The accepted remote execution input was erased.");

        GuildDataRef reference = new(Guid.Parse(reader.GetString(0)), GuildContentKind.ExecutionInput, 1,
            Convert.ToHexString((byte[])reader.GetValue(1)), "application/json", reader.GetInt64(2), false);

        byte[] body = (byte[])reader.GetValue(3);

        if (reader.GetInt32(4) != 0 || !SHA256.HashData(body).AsSpan().SequenceEqual((byte[])reader.GetValue(1))) return Missing;

        await reader.DisposeAsync().ConfigureAwait(false);

        var label = await GuildLocalExecutionStore.ValidateReferenceAsync(connection, transaction, reference, ct).ConfigureAwait(false);

        if (label.IsFailure) return label.Error;

        if (label.Value is not null) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "Protected remote input requires a qualified disclosure adapter.");

        try
        {
            GuildRemoteExecutionInput? input = JsonSerializer.Deserialize(body, GuildStorageJsonContext.Default.GuildRemoteExecutionInput);

            return Valid(input) ? input! : Missing;
        }
        catch (JsonException)
        {
            return Missing;
        }
    }

    internal static async Task<Result<GuildRemoteRecoveryTarget>> ReadRecoveryWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid questId, CancellationToken ct)
    {
        await using SqliteCommand query = Command(connection, transaction, """
            SELECT RecoveryDiscoveryEndpoint,RecoveryTransport,RecoveryDigest FROM guild_remote_executions
            WHERE QuestId=$quest AND typeof(RecoveryDiscoveryEndpoint)='text' AND typeof(RecoveryTransport)='blob'
                AND typeof(RecoveryDigest)='blob' AND length(RecoveryDigest)=32
                AND length(CAST(RecoveryDiscoveryEndpoint AS BLOB))+length(RecoveryTransport)<=65536
            """, questId);

        await using SqliteDataReader reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Missing;

        string discovery = reader.GetString(0);

        byte[] body = (byte[])reader.GetValue(1);

        if (!RecoveryDigest(System.Text.Encoding.UTF8.GetBytes(discovery), body).AsSpan().SequenceEqual((byte[])reader.GetValue(2))) return Missing;

        try
        {
            GuildRemoteTransportDescriptor? transport = JsonSerializer.Deserialize(body, GuildJsonContext.Default.GuildRemoteTransportDescriptor);

            if (transport is null || !ValidTransport(transport) || !ValidUrl(discovery)
                || !body.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(transport, GuildJsonContext.Default.GuildRemoteTransportDescriptor)))
                return Missing;

            return new GuildRemoteRecoveryTarget(discovery, transport);
        }
        catch (JsonException)
        {
            return Missing;
        }
    }

    private static byte[] RecoveryDigest(byte[] discovery, byte[] transport)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(discovery);
        hash.AppendData([0]);
        hash.AppendData(transport);

        return hash.GetHashAndReset();
    }

    internal static bool ValidTransport(GuildRemoteTransportDescriptor value) => !string.IsNullOrWhiteSpace(value.PeerId)
        && ValidUrl(value.Endpoint) && !string.IsNullOrWhiteSpace(value.ProtocolVersion) && !value.Interfaces.IsDefaultOrEmpty
        && value.Interfaces.All(item => !string.IsNullOrWhiteSpace(item)) && !value.AuthenticationSchemes.IsDefault;

    private static bool Valid(GuildRemoteExecutionInput? value) => value is { Version: 1 } && ValidUrl(value.DiscoveryEndpoint)
        && ValidTransport(value.Transport) && Enum.IsDefined(value.Mode) && !value.AcceptedOutputModes.IsDefaultOrEmpty
        && value.AcceptedOutputModes.All(item => !string.IsNullOrWhiteSpace(item)) && !value.DelegationChain.IsDefault
        && value.DelegationChain.All(item => !string.IsNullOrWhiteSpace(item));

    private static bool ValidUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, Guid questId)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        command.Parameters.AddWithValue("$quest", Id(questId));

        return command;
    }

    internal static string Id(Guid value) => value.ToString("D").ToUpperInvariant();

    private static readonly Error Missing = new(ErrorCodes.Guild.ReconciliationRequired, "The accepted remote execution binding is missing or inconsistent.");
}
