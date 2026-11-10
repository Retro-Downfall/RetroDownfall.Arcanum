using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildInboundBinding(string TaskId, Guid QuestId, string PeerId, string GrantRevision,
    Guid AuthorityEpoch, string MessageId, string ContextId, string RequestDigest);

internal sealed record GuildInboundIssue(GuildInboundPeer Peer, string TaskId, string MessageId, string ContextId,
    string RequestDigest, ImmutableArray<string>? DelegationChain = null);

/// <summary>Exact transport identity receipts committed inside the sole Quest issuance transaction.</summary>
internal static class GuildInboundTaskStore
{
    internal static Guid Identity(string kind, string peerId, string messageId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"guild.inbound.v1\n{kind.Length}:{kind}{peerId.Length}:{peerId}{messageId.Length}:{messageId}"));

        return new Guid(digest.AsSpan(0, 16));
    }

    internal static bool ValidIdentifier(string? value) => !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= 4096;

    internal static async Task<Result<GuildInboundBinding?>> ReadWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, string peerId, string? taskId, string? messageId, CancellationToken ct)
    {
        await using SqliteCommand read = connection.CreateCommand();

        read.Transaction = transaction;

        read.CommandText = """
            SELECT TaskId,QuestId,PeerId,GrantRevision,AuthorityEpoch,RemoteMessageId,ContextId,RequestDigest
            FROM guild_inbound_tasks WHERE PeerId=$peer AND (TaskId=$task OR RemoteMessageId=$message)
              AND typeof(TaskId)='text' AND length(CAST(TaskId AS BLOB)) BETWEEN 1 AND 4096
              AND typeof(ContextId)='text' AND length(CAST(ContextId AS BLOB)) BETWEEN 1 AND 4096
              AND typeof(RemoteMessageId)='text' AND length(CAST(RemoteMessageId AS BLOB)) BETWEEN 1 AND 4096
              AND typeof(GrantRevision)='text' AND length(GrantRevision)=64
              AND typeof(RequestDigest)='blob' AND length(RequestDigest)=32
            """;

        read.Parameters.AddWithValue("$peer", peerId);

        read.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);

        read.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildInboundBinding?>.Success(null);

        if (!Guid.TryParseExact(reader.GetString(1), "D", out Guid quest) || quest == Guid.Empty
            || !Guid.TryParseExact(reader.GetString(4), "D", out Guid epoch) || epoch == Guid.Empty)
            return new Error(ErrorCodes.Guild.Unavailable, "The inbound task binding is unavailable.");

        GuildInboundBinding binding = new(reader.GetString(0), quest, reader.GetString(2), reader.GetString(3), epoch,
            reader.GetString(5), reader.GetString(6), Convert.ToHexString((byte[])reader.GetValue(7)));

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new Error(ErrorCodes.Guild.Unavailable, "The inbound task binding is ambiguous.") : binding;
    }

    internal static async Task<Result> CreateWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildLocalHostPolicy policy, GuildQuestStored quest, GuildInboundIssue issue, CancellationToken ct)
    {
        if (!ValidIdentifier(issue.TaskId) || !ValidIdentifier(issue.MessageId) || !ValidIdentifier(issue.ContextId)
            || issue.RequestDigest.Length != 64 || quest.Quest.RootQuestId != quest.Quest.Id)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The inbound task identity is invalid.");

        Result<Guid> epoch = await policy.EnsureInboundPeerWithinAsync(connection, transaction, issue.Peer, ct).ConfigureAwait(false);

        if (epoch.IsFailure) return epoch.Error;

        await using SqliteCommand write = connection.CreateCommand();

        write.Transaction = transaction;

        write.CommandText = """
            INSERT INTO guild_inbound_tasks(TaskId,QuestId,PeerId,GrantRevision,AuthorityEpoch,RemoteMessageId,ContextId,RequestDigest)
            VALUES($task,$quest,$peer,$revision,$epoch,$message,$context,$digest)
            """;

        write.Parameters.AddWithValue("$task", issue.TaskId);

        write.Parameters.AddWithValue("$quest", quest.Quest.Id.ToString("D").ToUpperInvariant());

        write.Parameters.AddWithValue("$peer", issue.Peer.PeerId);

        write.Parameters.AddWithValue("$revision", issue.Peer.GrantRevision);

        write.Parameters.AddWithValue("$epoch", epoch.Value.ToString("D").ToUpperInvariant());

        write.Parameters.AddWithValue("$message", issue.MessageId);

        write.Parameters.AddWithValue("$context", issue.ContextId);

        write.Parameters.AddWithValue("$digest", Convert.FromHexString(issue.RequestDigest));

        _ = await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }
}
