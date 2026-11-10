using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildLocalHostPolicy
{
    private volatile bool _inboundPersistenceAvailable = true;

    internal void SetInboundPersistenceAvailable(bool available) => _inboundPersistenceAvailable = available;

    internal Task<Result<bool>> HasInboundRootsAsync(CancellationToken ct) => ReadInboundExistenceAsync(null, ct);

    internal Task<Result<bool>> IsInboundTaskAsync(string taskId, CancellationToken ct) => ReadInboundExistenceAsync(taskId, ct);

    private async Task<Result<bool>> ReadInboundExistenceAsync(string? taskId, CancellationToken ct)
    {
        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return Unavailable;

        await using IGrimoireWorkLease work = acquired;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        var opened = await connections.OpenFreshAsync(GrimoireOrdinaryFreshConnectionKind.ReadOnly, lifetime.Token).ConfigureAwait(false);

        if (opened.IsFailure) return opened.Error;

        await using IGrimoireOrdinaryConnectionLease lease = opened.Value;

        await using SqliteCommand read = lease.Connection.CreateCommand();

        read.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='guild_inbound_tasks')";

        if (!Equals(1L, await read.ExecuteScalarAsync(lifetime.Token).ConfigureAwait(false)))
        {
            read.CommandText = "SELECT EXISTS(SELECT 1 FROM grimoire_feature_schemas WHERE FamilyCode=2 AND TransactionTierCode=3 AND SchemaVersion>=11)";

            return Equals(1L, await read.ExecuteScalarAsync(lifetime.Token).ConfigureAwait(false)) ? Unavailable : false;
        }

        read.CommandText = taskId is null ? "SELECT EXISTS(SELECT 1 FROM guild_inbound_tasks)"
            : "SELECT EXISTS(SELECT 1 FROM guild_inbound_tasks WHERE TaskId=$task)";

        if (taskId is not null) read.Parameters.AddWithValue("$task", taskId);

        return Equals(1L, await read.ExecuteScalarAsync(lifetime.Token).ConfigureAwait(false));
    }

    internal async Task<Result<GuildCallerContext>> CreatePeerCallerAsync(GuildInboundPeer peer, Guid invocationId, CancellationToken ct)
    {
        if (!settings.CurrentValue.Features.Guild || !_inboundPersistenceAvailable || peer is null || invocationId == Guid.Empty || inboundPeers?.Authenticates(peer) != true) return Forbidden;

        Result initialized = await InitializeIdentityAsync(ct).ConfigureAwait(false);

        if (initialized.IsFailure) return initialized.Error;

        GuildCallerContext caller = new(peer.PeerId, peer.GrantRevision, invocationId, InstallationGeneration);

        _callers.Add(caller, peer);

        return caller;
    }

    internal Task<Result<GuildInboundBinding?>> ReadInboundBindingAsync(GuildInboundPeer peer, string? taskId, string? messageId, CancellationToken ct)
    {
        if (inboundPeers?.Authenticates(peer) != true) return Task.FromResult(Result<GuildInboundBinding?>.Failure(Forbidden));

        return ExecuteAsync<GuildInboundBinding?>(false, (connection, transaction, token) =>
            GuildInboundTaskStore.ReadWithinAsync(connection, transaction, peer.PeerId, taskId, messageId, token), ct);
    }

    internal Task<Result<string[]>> ReadInboundTaskIdsAsync(GuildInboundPeer peer, string? afterTaskId, int limit, CancellationToken ct)
    {
        if (!_inboundPersistenceAvailable || inboundPeers?.Authenticates(peer) != true)
            return Task.FromResult(Result<string[]>.Failure(Forbidden));

        return ExecuteAsync<string[]>(false, async (connection, transaction, token) =>
        {
            await using SqliteCommand read = Command(connection, transaction, """
                SELECT TaskId FROM guild_inbound_tasks WHERE PeerId=$peer AND TaskId>$after COLLATE BINARY
                  AND typeof(TaskId)='text' AND length(CAST(TaskId AS BLOB)) BETWEEN 1 AND 4096
                ORDER BY TaskId COLLATE BINARY LIMIT $limit
                """);

            Add(read, "$peer", peer.PeerId);

            Add(read, "$after", afterTaskId ?? string.Empty);

            Add(read, "$limit", Math.Clamp(limit, 1, 100));

            List<string> ids = [];

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false)) ids.Add(reader.GetString(0));

            return ids.ToArray();
        }, ct);
    }

    private static bool SameGrant(GuildGrant left, GuildGrant right)
    {
        Result<string> a = GuildQuestIntent.Compute(new(Guid.Parse("11111111-1111-1111-1111-111111111111"), null, null, "grant", left));

        Result<string> b = GuildQuestIntent.Compute(new(Guid.Parse("11111111-1111-1111-1111-111111111111"), null, null, "grant", right));

        return a.IsSuccess && b.IsSuccess && a.Value == b.Value;
    }

    internal async Task<Result<Guid>> EnsureInboundPeerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildInboundPeer peer, CancellationToken ct)
    {
        if (inboundPeers?.Authenticates(peer) != true) return Forbidden;

        Guid epoch = Guid.NewGuid();

        await using (SqliteCommand read = Command(connection, transaction,
            "SELECT GrantRevision,AuthorityEpoch,HostInstanceId,AuthorityVersion FROM guild_inbound_peer_state WHERE PeerId=$peer"))
        {
            Add(read, "$peer", peer.PeerId);

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                bool sameHost = reader.GetString(2) == Id(inboundPeers.HostInstanceId);

                bool unchanged = reader.GetString(0) == peer.GrantRevision
                    && (sameHost ? reader.GetInt64(3) == peer.AuthorityVersion : peer.AuthorityVersion == 0);

                if (unchanged && Guid.TryParseExact(reader.GetString(1), "D", out Guid prior) && prior != Guid.Empty)
                    epoch = prior;
            }
        }

        await using SqliteCommand write = Command(connection, transaction, """
            INSERT INTO guild_inbound_peer_state(PeerId,GrantRevision,AuthorityEpoch,HostInstanceId,AuthorityVersion)
            VALUES($peer,$revision,$epoch,$host,$version)
            ON CONFLICT(PeerId) DO UPDATE SET GrantRevision=$revision,AuthorityEpoch=$epoch,HostInstanceId=$host,AuthorityVersion=$version
            """);

        Add(write, "$peer", peer.PeerId);

        Add(write, "$revision", peer.GrantRevision);

        Add(write, "$epoch", Id(epoch));

        Add(write, "$host", Id(inboundPeers.HostInstanceId));

        Add(write, "$version", peer.AuthorityVersion);

        _ = await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return epoch;
    }

    private async Task<Result> RequireInboundRootForPeerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, Guid questId, CancellationToken ct)
    {
        await using SqliteCommand read = Command(connection, transaction,
            "SELECT RootQuestId,PeerId,PeerGrantRevision FROM guild_signets WHERE QuestId=$quest AND Revoked=0 AND RestoredInert=0 AND Retired=0");

        Add(read, "$quest", Id(questId));

        Guid root;

        await using (SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(1) || reader.IsDBNull(2)
                || reader.GetString(1) != caller.PeerId || reader.GetString(2) != caller.PeerGrantRevision
                || !Guid.TryParseExact(reader.GetString(0), "D", out root)) return Forbidden;
        }

        // An inbound parent may address only the exact root task it issued, never arbitrary descendants.
        if (root != questId) return Forbidden;

        return await RequireInboundRootWithinAsync(connection, transaction, root, caller.PeerId!, caller.PeerGrantRevision!, ct).ConfigureAwait(false);
    }

    private async Task<Result> RequireInboundRootWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid rootId, string peerId, string revision, CancellationToken ct)
    {
        GuildInboundPeer? peer = inboundPeers?.Peers.FirstOrDefault(item => item.PeerId == peerId && item.GrantRevision == revision);

        if (!settings.CurrentValue.Features.Guild || !_inboundPersistenceAvailable || peer is null || inboundPeers?.IsCurrent(peerId, revision, peer.AuthorityVersion) != true
            || peer.Grant.ExpiresAtUtc is { } expiry && expiry <= time.GetUtcNow()) return Forbidden;

        // Issuance is already protected by the exact pending admission on this connection. Its ingress
        // binding commits later in this same transaction, after the canonical Quest has been inserted.
        if (_pending.TryGetValue(rootId, out Admission? pending) && pending.Transaction == transaction
            && pending.Owners.PeerId == peerId && pending.Owners.PeerGrantRevision == revision)
            return Result.Success();

        await using SqliteCommand read = Command(connection, transaction, """
            SELECT state.HostInstanceId,state.AuthorityVersion
            FROM guild_inbound_tasks task JOIN guild_inbound_peer_state state ON state.PeerId=task.PeerId
            JOIN guild_signets signet ON signet.QuestId=task.QuestId
            JOIN guild_signet_authority_state authority ON authority.Epoch=signet.AuthorityEpoch
            WHERE task.QuestId=$root AND task.PeerId=$peer AND task.GrantRevision=$revision
              AND task.AuthorityEpoch=state.AuthorityEpoch AND task.GrantRevision=state.GrantRevision
              AND signet.RootQuestId=task.QuestId AND signet.PeerId=task.PeerId
              AND signet.PeerGrantRevision=task.GrantRevision AND signet.Revoked=0
              AND signet.RestoredInert=0 AND signet.Retired=0
            """);

        Add(read, "$root", Id(rootId));

        Add(read, "$peer", peerId);

        Add(read, "$revision", revision);

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Forbidden;

        bool sameHost = reader.GetString(0) == Id(inboundPeers.HostInstanceId);

        return (sameHost ? reader.GetInt64(1) == peer.AuthorityVersion : peer.AuthorityVersion == 0)
            ? Result.Success() : Forbidden;
    }

    private async Task<Result<bool>> ExecuteInboundRootAsync(Guid root,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<Result<bool>>> action, CancellationToken ct)
    {
        if (!gate.TryAcquireWorkLease(GrimoireWorkKind.GuildStorage, out IGrimoireWorkLease? acquired) || acquired is null) return Unavailable;

        await using IGrimoireWorkLease work = acquired;

        if (!work.TryBeginExternalEffectGroup(out IGrimoireExternalEffectGroup? effect) || effect is null) return Unavailable;

        await using IGrimoireExternalEffectGroup heldEffect = effect;

        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, work.MaintenanceRevocation);

        return await GuildSignetRootGate.RunAsync<bool>(gate, root,
            token => _store.ExecuteAsync(true, action, token), lifetime.Token).ConfigureAwait(false);
    }

    /// <summary>Persists lifecycle invalidations, including transitions with no intervening peer request.</summary>
    internal async Task<Result> ReconcileInboundPeersAsync(CancellationToken ct,
        Func<Guid, CancellationToken, Task<Result>>? requestCancellation = null)
    {
        if (inboundPeers is null) return Result.Success();

        // Each admitted read advances at most 100 retained bindings; the cursor bounds one turn,
        // never the number of roots or the lifetime namespace. Revocation acquires one root gate only.
        var capturedDenials = inboundPeers.CapturePendingDenials();

        string after = string.Empty;

        while (true)
        {
            Result<(Guid? Root, string? NextTaskId, string? PeerId)> candidate = await ExecuteAsync<(Guid?, string?, string?)>(false,
                async (connection, transaction, token) =>
                {
                    await using SqliteCommand read = Command(connection, transaction, """
                        SELECT task.QuestId,state.PeerId,state.HostInstanceId,state.AuthorityVersion,
                            state.GrantRevision,task.GrantRevision,task.AuthorityEpoch,state.AuthorityEpoch,task.TaskId
                        FROM guild_inbound_tasks task JOIN guild_inbound_peer_state state ON state.PeerId=task.PeerId
                        JOIN guild_signets signet ON signet.QuestId=task.QuestId
                        WHERE signet.Revoked=0 AND signet.Retired=0 AND task.TaskId>$after COLLATE BINARY
                        ORDER BY task.TaskId COLLATE BINARY LIMIT 100
                        """);

                    Add(read, "$after", after);

                    int scanned = 0;

                    string? through = null;

                    await using SqliteDataReader reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);

                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        scanned++;

                        through = reader.GetString(8);

                        string peer = reader.GetString(1);

                        inboundPeers.RememberPeerId(peer);

                        long version = inboundPeers.GetAuthorityVersion(peer);

                        bool changed = inboundPeers.IsPendingDenial(peer) || !inboundPeers.MatchesAuthority(peer, reader.GetString(4))
                            || reader.GetString(4) != reader.GetString(5) || reader.GetString(6) != reader.GetString(7)
                            || (reader.GetString(2) == Id(inboundPeers.HostInstanceId)
                                ? reader.GetInt64(3) != version : version != 0);

                        if (changed)
                        {
                            if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid root) || root == Guid.Empty) return Unavailable;

                            return ((Guid?)root, through, peer);
                        }
                    }

                    return ((Guid?)null, scanned == 100 ? through : null, (string?)null);
                }, ct).ConfigureAwait(false);

            if (candidate.IsFailure) return candidate.Error;

            var next = candidate.Value;

            if (next.Root is not { } rootId)
            {
                if (next.NextTaskId is null) return inboundPeers.ClearReconciledDenials(capturedDenials);

                after = next.NextTaskId;

                continue;
            }

            Result recordedDenial = inboundPeers.PersistDenial(next.PeerId!);

            if (recordedDenial.IsFailure) return recordedDenial;

            if (requestCancellation is not null)
            {
                Result cancellation = await requestCancellation(rootId, ct).ConfigureAwait(false);

                if (cancellation.IsFailure) return cancellation;
            }

            Result<bool> revoked = await ExecuteInboundRootAsync(rootId, async (connection, transaction, inner) =>
                {
                    await using SqliteCommand write = Command(connection, transaction, """
                        UPDATE guild_signets SET Revoked=1,CancellationRequested=1,
                            RevocationGeneration=RevocationGeneration+1,Revision=Revision+1
                        WHERE RootQuestId=$root AND Revoked=0;
                        UPDATE guild_quests SET CancellationRequested=1,Revision=Revision+1,UpdatedAtUtc=$now
                        WHERE Id=$root AND CancellationRequested=0;
                        """);

                    Add(write, "$root", Id(rootId));

                    Add(write, "$now", UtcInstantText.Format(time.GetUtcNow()));

                    _ = await write.ExecuteNonQueryAsync(inner).ConfigureAwait(false);

                    return true;
                }, ct).ConfigureAwait(false);

            if (revoked.IsFailure) return revoked.Error;

            after = next.NextTaskId!;
        }
    }
}
