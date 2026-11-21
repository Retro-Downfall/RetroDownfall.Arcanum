using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal Task<Result<GuildPortalMessageDeliveryReceipt>> ObserveRemotePortalMessageAsync(GuildQuestDriver driver,
        GuildRemotePortalWork work, Func<GuildPortalMessageReceiptQuery, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> read,
        CancellationToken ct) => ObserveRemotePortalCoreAsync(driver.Execution.RootQuestId, driver.Execution.QuestId, work, read,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, driver, true, true, token)
                    .ConfigureAwait(false);

                return admitted.IsFailure ? admitted.Error : Result.Success();
            }, ct);

    internal async Task<Result<GuildRemotePortalObservation?>> ReadTerminalRemotePortalObservationAsync(GuildCallerContext owner,
        Guid questId, CancellationToken ct)
    {
        if (owner.Kind != GuildCallerKind.Operator) return Forbidden;

        var located = await quests.GetAsync(owner, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        Guid root = located.Value.Quest.RootQuestId;

        return await authority.InQuestTransactionAsync<GuildRemotePortalObservation?>(root,
            async (connection, transaction, _, token) =>
            {
                Result owned = await RequireTerminalPortalOwnerWithinAsync(connection, transaction, owner, root, questId, token)
                    .ConfigureAwait(false);

                if (owned.IsFailure) return owned.Error;

                var work = await SelectRemotePortalWorkWithinAsync(connection, transaction, questId, GuildRemotePortalWorkKind.Observe, token)
                    .ConfigureAwait(false);

                if (work.IsFailure) return work.Error;

                if (work.Value is not { } next) return Result<GuildRemotePortalObservation?>.Success(null);

                var row = await RequirePortalWorkWithinAsync(connection, transaction, questId, next, token).ConfigureAwait(false);

                if (row.IsFailure) return row.Error;

                var target = await ValidatePortalTargetWithinAsync(connection, transaction, row.Value, token).ConfigureAwait(false);

                return target.IsFailure ? target.Error : new GuildRemotePortalObservation(next, target.Value.Route);
            }, ct).ConfigureAwait(false);
    }

    internal async Task<Result<GuildPortalMessageDeliveryReceipt>> ObserveTerminalRemotePortalMessageAsync(GuildCallerContext owner,
        Guid questId, GuildRemotePortalWork work, Func<GuildPortalMessageReceiptQuery, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> read,
        CancellationToken ct)
    {
        if (owner.Kind != GuildCallerKind.Operator) return Forbidden;

        var located = await quests.GetAsync(owner, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        Guid root = located.Value.Quest.RootQuestId;

        return await ObserveRemotePortalCoreAsync(root, questId, work, read,
            (connection, transaction, _, token) => RequireTerminalPortalOwnerWithinAsync(connection, transaction, owner, root, questId, token), ct)
            .ConfigureAwait(false);
    }

    private async Task<Result> RequireTerminalPortalOwnerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext owner, Guid root, Guid questId, CancellationToken ct)
    {
        if (owner.Kind != GuildCallerKind.Operator) return Forbidden;

        var row = await GuildQuestStore.ReadWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false);

        if (row.IsFailure) return row.Error;

        if (row.Value.Quest.RootQuestId != root || !ClosedRemoteRecipient(row.Value.Quest)) return Forbidden;

        return await quests.RequireVisibleAsync(connection, transaction, owner, row.Value, ct).ConfigureAwait(false);
    }

    private async Task<Result<GuildPortalMessageDeliveryReceipt>> ObserveRemotePortalCoreAsync(Guid root, Guid questId,
        GuildRemotePortalWork work, Func<GuildPortalMessageReceiptQuery, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> read,
        Func<SqliteConnection, SqliteTransaction, SignetAuthority.QuestTransaction, CancellationToken, Task<Result>> authorize,
        CancellationToken ct)
    {
        if (work.Kind != GuildRemotePortalWorkKind.Observe) return ClaimConflict;

        GuildPortalMessageReceiptQuery? query = null;

        var observed = await authority.AdmitQuestEffectAsync<GuildPortalMessageDeliveryReceipt>(root,
            async (connection, transaction, signets, token) =>
            {
                Result allowed = await authorize(connection, transaction, signets, token).ConfigureAwait(false);

                if (allowed.IsFailure) return allowed.Error;

                var row = await RequirePortalWorkWithinAsync(connection, transaction, questId, work, token).ConfigureAwait(false);

                if (row.IsFailure) return row.Error;

                var target = await ValidatePortalTargetWithinAsync(connection, transaction, row.Value, token).ConfigureAwait(false);

                if (target.IsFailure) return target.Error;

                await using SqliteCommand note = Command(connection, transaction, """
                    UPDATE guild_remote_portal_deliveries SET ObservationCount=ObservationCount+1
                    WHERE InboxId=$inbox AND TransportStateCode IN(1,2) AND ObservationCount<9223372036854775807
                    """);

                Add(note, "$inbox", Id(work.InboxId));

                if (await note.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Exhausted;

                query = row.Value.Query;

                return true;
            }, token => read(query!, token), ct).ConfigureAwait(false);

        if (observed.IsFailure || query is null) return observed;

        return await RecordRemotePortalReceiptAsync(root, questId, query, observed.Value, ct).ConfigureAwait(false);
    }

    private Task<Result<GuildPortalMessageDeliveryReceipt>> RecordRemotePortalReceiptAsync(Guid root, Guid questId,
        GuildPortalMessageReceiptQuery expected, GuildPortalMessageDeliveryReceipt receipt, CancellationToken ct)
    {
        if (receipt is null || receipt.TaskId != expected.TaskId || receipt.ContextId != expected.ContextId
            || receipt.MessageId != expected.MessageId || receipt.IntentDigest != expected.IntentDigest || receipt.ReceiptId == Guid.Empty
            || receipt.Revision < 1 || receipt.DeliveryState is not (GuildDeliveryState.Pending or GuildDeliveryState.Applied or GuildDeliveryState.Withheld)
            || (receipt.DeliveryState == GuildDeliveryState.Applied) != (receipt.ApplicationReceiptId is { } applied && applied != Guid.Empty)
            || (receipt.DeliveryState != GuildDeliveryState.Applied && receipt.ApplicationReceiptId is not null))
            return Task.FromResult(Result<GuildPortalMessageDeliveryReceipt>.Failure(Conflict));

        return authority.InQuestTransactionAsync<GuildPortalMessageDeliveryReceipt>(root,
            async (connection, transaction, _, token) =>
            {
                var found = await ReadRemotePortalDeliveryWithinAsync(connection, transaction, questId, expected.MessageId, token)
                    .ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                RemotePortalDelivery row = found.Value;

                if (row.TransportState == 0 || row.Query != expected || row.PeerReceiptId is { } priorId && priorId != receipt.ReceiptId)
                    return Conflict;

                var target = await ValidatePortalTargetWithinAsync(connection, transaction, row, token).ConfigureAwait(false);

                if (target.IsFailure) return target.Error;

                if (row.PeerRevision is { } priorRevision)
                {
                    if (receipt.Revision < priorRevision) return Conflict;

                    if (receipt.Revision == priorRevision)
                        return row.PeerState == receipt.DeliveryState && row.PeerApplicationReceiptId == receipt.ApplicationReceiptId
                            ? receipt : Conflict;
                }

                if (row.ApplicationState != GuildDeliveryState.Pending)
                    return row.PeerState == receipt.DeliveryState && row.PeerApplicationReceiptId == receipt.ApplicationReceiptId
                        ? receipt : Conflict;

                await using SqliteCommand save = Command(connection, transaction, """
                    UPDATE guild_remote_portal_deliveries
                    SET TransportStateCode=2,PeerReceiptId=$peer,PeerRevision=$revision,PeerDeliveryStateCode=$state,
                        PeerApplicationReceiptId=$applied
                    WHERE InboxId=$inbox AND TransportStateCode IN(1,2)
                    """);

                Add(save, "$inbox", Id(row.InboxId));

                Add(save, "$peer", Id(receipt.ReceiptId));

                Add(save, "$revision", receipt.Revision);

                Add(save, "$state", (int)receipt.DeliveryState);

                Add(save, "$applied", Id(receipt.ApplicationReceiptId));

                if (await save.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return ClaimConflict;

                if (receipt.DeliveryState == GuildDeliveryState.Pending)
                {
                    if (row.TransportState == 2) return receipt;

                    Result reported = await AdvanceRemotePendingInboxWithinAsync(connection, transaction, row, root, token).ConfigureAwait(false);

                    return reported.IsFailure ? reported.Error : receipt;
                }

                if (receipt.DeliveryState == GuildDeliveryState.Withheld)
                {
                    Result withheld = await GuildPortalWithholding.WithholdWithinAsync(connection, transaction, string.Empty,
                        "i.Id=$identity", row.InboxId, ErrorCodes.Guild.Rejected, time.GetUtcNow(), token,
                        updateResultOutbox: false, authorizedRemoteDisposition: true).ConfigureAwait(false);

                    return withheld.IsFailure ? withheld.Error : receipt;
                }

                var committed = await CommitInboxWithinAsync(connection, transaction, row.InboxId, row.EnvelopeId, row.Revision,
                    Guid.NewGuid(), 0, null, time.GetUtcNow(), token).ConfigureAwait(false);

                return committed.IsFailure ? committed.Error : receipt;
            }, ct);
    }

    private static async Task<Result<RemotePortalDelivery>> ReadRemotePortalDeliveryWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid recipient, Guid inbox, CancellationToken ct)
    {
        await using SqliteCommand read = Command(connection, transaction, """
            SELECT CASE WHEN length(CAST(d.EnvelopeId AS BLOB))=36 THEN d.EnvelopeId END,
                CASE WHEN length(CAST(i.StreamId AS BLOB))=36 THEN i.StreamId END,i.SenderSequence,i.Revision,d.TransportStateCode,
                CASE WHEN typeof(d.TaskId)='text' AND length(CAST(d.TaskId AS BLOB)) BETWEEN 1 AND 4096 THEN d.TaskId END,
                CASE WHEN typeof(d.ContextId)='text' AND length(CAST(d.ContextId AS BLOB)) BETWEEN 1 AND 4096 THEN d.ContextId END,
                CASE WHEN typeof(d.TargetDigest)='blob' AND length(d.TargetDigest)=32 THEN d.TargetDigest END,
                CASE WHEN typeof(d.WireIntentDigest)='blob' AND length(d.WireIntentDigest)=32 THEN hex(d.WireIntentDigest) END,
                CASE WHEN length(CAST(d.PeerReceiptId AS BLOB))=36 THEN d.PeerReceiptId END,d.PeerRevision,d.PeerDeliveryStateCode,
                CASE WHEN length(CAST(d.PeerApplicationReceiptId AS BLOB))=36 THEN d.PeerApplicationReceiptId END,i.DeliveryStateCode
            FROM guild_remote_portal_deliveries d JOIN guild_inbox i ON i.Id=d.InboxId
                AND i.RecipientQuestId=d.RecipientQuestId AND i.EnvelopeId=d.EnvelopeId
            WHERE d.InboxId=$inbox AND d.RecipientQuestId=$quest AND i.RecipientKey=('quest:' || d.RecipientQuestId)
            """);

        Add(read, "$inbox", Id(inbox));

        Add(read, "$quest", Id(recipient));

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return NoInbox;

        if (!ReadRemoteReceiptId(reader, 0, out Guid envelope) || !ReadRemoteReceiptId(reader, 1, out Guid stream)
            || reader.GetValue(2) is not long sequence || sequence < 1 || reader.GetValue(3) is not long revision || revision < 1
            || reader.GetValue(4) is not long state || state is < 0 or > 2 || reader.IsDBNull(5) || reader.IsDBNull(6)
            || reader.GetValue(7) is not byte[] digest || reader.GetValue(13) is not long application || application is < 1 or > 3)
            return Unavailable;

        string? wire = reader.IsDBNull(8) ? null : reader.GetString(8);

        if ((state == 0) == (wire is not null)) return Unavailable;

        Guid? remoteReceipt = null;

        long? remoteRevision = null;

        GuildDeliveryState? remoteState = null;

        Guid? remoteApplication = null;

        if (state == 2)
        {
            if (!ReadRemoteReceiptId(reader, 9, out Guid peer) || reader.GetValue(10) is not long peerRevision || peerRevision < 1
                || reader.GetValue(11) is not long peerState || peerState is < 1 or > 3) return Unavailable;

            remoteReceipt = peer;
            remoteRevision = peerRevision;
            remoteState = (GuildDeliveryState)peerState;

            if (remoteState == GuildDeliveryState.Applied)
            {
                if (!ReadRemoteReceiptId(reader, 12, out Guid applied)) return Unavailable;

                remoteApplication = applied;
            }
        }

        return new RemotePortalDelivery(inbox, recipient, envelope, stream, sequence, revision, (int)state,
            reader.GetString(5), reader.GetString(6), digest, wire, remoteReceipt, remoteRevision, remoteState, remoteApplication,
            (GuildDeliveryState)application);
    }

    private static bool ReadRemoteReceiptId(SqliteDataReader reader, int ordinal, out Guid id)
    {
        id = Guid.Empty;

        return !reader.IsDBNull(ordinal) && Guid.TryParseExact(reader.GetString(ordinal), "D", out id) && id != Guid.Empty;
    }
}
