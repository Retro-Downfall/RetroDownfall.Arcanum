using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    private sealed record RemotePortalDelivery(Guid InboxId, Guid Recipient, Guid EnvelopeId, Guid StreamId, long Sequence,
        long Revision, int TransportState, string TaskId, string ContextId, byte[] TargetDigest, string? WireDigest,
        Guid? PeerReceiptId, long? PeerRevision, GuildDeliveryState? PeerState, Guid? PeerApplicationReceiptId,
        GuildDeliveryState ApplicationState)
    {
        internal GuildPortalMessageReceiptQuery Query =>
            new(TaskId, ContextId, InboxId, WireDigest!);
    }

    internal Task<Result<GuildRemotePortalWork?>> ReadRemotePortalWorkAsync(GuildQuestDriver driver,
        GuildRemotePortalWorkKind? preferred, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildRemotePortalWork?>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, driver, true, true, token)
                    .ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                bool observeOnly = ClosedRemoteRecipient(admitted.Value.Row.Quest);

                if (observeOnly && preferred == GuildRemotePortalWorkKind.Send) return Result<GuildRemotePortalWork?>.Success(null);

                return await SelectRemotePortalWorkWithinAsync(connection, transaction, driver.Execution.QuestId,
                    observeOnly ? GuildRemotePortalWorkKind.Observe : preferred, token).ConfigureAwait(false);
            }, ct);

    internal async Task<Result<GuildPortalMessageDeliveryReceipt>> SendRemotePortalMessageAsync(GuildQuestDriver driver,
        GuildRemotePortalWork work, Func<GuildPortalMessageRequest, CancellationToken, Task<Result<GuildPortalMessageDeliveryReceipt>>> send,
        CancellationToken ct)
    {
        if (work.Kind != GuildRemotePortalWorkKind.Send) return ClaimConflict;

        GuildPortalMessageRequest? request = null;

        var result = await authority.AdmitQuestEffectAsync<GuildPortalMessageDeliveryReceipt>(driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token)
                    .ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (ClosedRemoteRecipient(admitted.Value.Row.Quest)) return ClosedRecipient;

                var delivery = await RequirePortalWorkWithinAsync(connection, transaction, driver.Execution.QuestId, work, token)
                    .ConfigureAwait(false);

                if (delivery.IsFailure) return delivery.Error;

                var target = await ValidatePortalTargetWithinAsync(connection, transaction, delivery.Value, token).ConfigureAwait(false);

                if (target.IsFailure) return target.Error;

                var envelope = await ReadEnvelopeWithinAsync(connection, transaction, delivery.Value.EnvelopeId, token).ConfigureAwait(false);

                if (envelope.IsFailure) return envelope.Error;

                if (envelope.Value.RootQuestId != driver.Execution.RootQuestId) return Forbidden;

                var source = await signets.ValidateCurrentGrantAsync(envelope.Value.QuestId, token).ConfigureAwait(false);

                if (source.IsFailure) return source.Error;

                Result ordinary = await RequireOrdinaryRemoteMessageWithinAsync(connection, transaction, envelope.Value.ContentRef.ContentId, token)
                    .ConfigureAwait(false);

                if (ordinary.IsFailure) return ordinary.Error;

                var caller = await DriverCallerWithinAsync(connection, transaction, driver, token).ConfigureAwait(false);

                if (caller.IsFailure) return caller.Error;

                var body = await ReadBodyWithinAsync(connection, transaction, signets, caller.Value, envelope.Value, token).ConfigureAwait(false);

                if (body.IsFailure) return body.Error;

                RemotePortalDelivery row = delivery.Value;

                GuildPortalMessageRequest unsigned = new(row.TaskId, row.ContextId, row.InboxId, row.StreamId, row.Sequence,
                    envelope.Value.ContentRef.MimeType, StrictUtf8.GetString(body.Value), "");

                var digest = GuildPortalMessageProtocol.ComputeIntentDigest(unsigned);

                if (digest.IsFailure) return digest.Error;

                request = unsigned with { IntentDigest = digest.Value };

                return await MarkRemotePortalPossiblySentWithinAsync(connection, transaction, row, envelope.Value.RootQuestId, digest.Value, token)
                    .ConfigureAwait(false);
            }, token => send(request!, token), ct).ConfigureAwait(false);

        if (result.IsFailure || request is null) return result;

        return await RecordRemotePortalReceiptAsync(driver.Execution.RootQuestId, driver.Execution.QuestId,
            request.ReceiptQuery(), result.Value, ct).ConfigureAwait(false);
    }

    private static async Task<Result<GuildRemotePortalWork?>> SelectRemotePortalWorkWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid recipient, GuildRemotePortalWorkKind? preferred, CancellationToken ct)
    {
        await using SqliteCommand read = Command(connection, transaction, """
            SELECT CASE WHEN typeof(d.InboxId)='text' AND length(CAST(d.InboxId AS BLOB))=36 THEN d.InboxId END,
                i.Revision,d.TransportStateCode
            FROM guild_remote_portal_deliveries d JOIN guild_inbox i ON i.Id=d.InboxId
            WHERE d.RecipientQuestId=$quest AND i.DeliveryStateCode=1
              AND (($kind IS NULL OR $kind=1) AND d.TransportStateCode IN(1,2)
                OR ($kind IS NULL OR $kind=0) AND d.TransportStateCode=0 AND NOT EXISTS(
                    SELECT 1 FROM guild_inbox earlier LEFT JOIN guild_remote_portal_deliveries prior ON prior.InboxId=earlier.Id
                    WHERE earlier.RecipientQuestId=i.RecipientQuestId AND earlier.StreamId=i.StreamId
                      AND earlier.SenderSequence<i.SenderSequence AND earlier.DeliveryStateCode=1
                      AND (prior.TransportStateCode IS NULL OR prior.TransportStateCode<2)))
            ORDER BY CASE WHEN d.TransportStateCode=0 THEN 0 ELSE 1 END,d.ObservationCount,i.SenderSequence,i.Id LIMIT 1
            """);

        Add(read, "$quest", Id(recipient));

        Add(read, "$kind", preferred is { } kind ? (int)kind : null);

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildRemotePortalWork?>.Success(null);

        if (reader.IsDBNull(0) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty
            || reader.GetValue(1) is not long revision || revision < 1 || reader.GetValue(2) is not long state || state is < 0 or > 2)
            return Unavailable;

        return new GuildRemotePortalWork(id, revision, state == 0 ? GuildRemotePortalWorkKind.Send : GuildRemotePortalWorkKind.Observe);
    }

    private static async Task<Result<RemotePortalDelivery>> RequirePortalWorkWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid recipient, GuildRemotePortalWork expected, CancellationToken ct)
    {
        var row = await ReadRemotePortalDeliveryWithinAsync(connection, transaction, recipient, expected.InboxId, ct).ConfigureAwait(false);

        if (row.IsFailure) return row.Error;

        if (row.Value.Revision != expected.Revision || row.Value.ApplicationState != GuildDeliveryState.Pending
            || (row.Value.TransportState == 0) != (expected.Kind == GuildRemotePortalWorkKind.Send)) return ClaimConflict;

        if (expected.Kind != GuildRemotePortalWorkKind.Send) return row;

        await using SqliteCommand predecessors = Command(connection, transaction, """
            SELECT 1 FROM guild_inbox earlier LEFT JOIN guild_remote_portal_deliveries prior ON prior.InboxId=earlier.Id
            WHERE earlier.RecipientQuestId=$quest AND earlier.StreamId=$stream AND earlier.SenderSequence<$sequence
              AND earlier.DeliveryStateCode=1 AND (prior.TransportStateCode IS NULL OR prior.TransportStateCode<2) LIMIT 1
            """);

        Add(predecessors, "$quest", Id(recipient));

        Add(predecessors, "$stream", Id(row.Value.StreamId));

        Add(predecessors, "$sequence", row.Value.Sequence);

        return await predecessors.ExecuteScalarAsync(ct).ConfigureAwait(false) is null ? row : ClaimConflict;
    }

    private async Task<Result<RemotePortalTarget>> ValidatePortalTargetWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, RemotePortalDelivery row, CancellationToken ct)
    {
        var current = await ReadPortalTargetWithinAsync(connection, transaction, row.Recipient, ct).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        return current.Value.TaskId == row.TaskId && current.Value.ContextId == row.ContextId
            && current.Value.Digest.AsSpan().SequenceEqual(row.TargetDigest)
            ? current
            : new Error(ErrorCodes.Guild.DestinationChanged, "The informational message's frozen peer task binding changed.");
    }

    private async Task<Result<bool>> MarkRemotePortalPossiblySentWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        RemotePortalDelivery row, Guid root, string digest, CancellationToken ct)
    {
        await using SqliteCommand save = Command(connection, transaction, """
            UPDATE guild_remote_portal_deliveries SET TransportStateCode=1,WireIntentDigest=$digest
            WHERE InboxId=$inbox AND TransportStateCode=0
            """);

        Add(save, "$digest", Convert.FromHexString(digest));

        Add(save, "$inbox", Id(row.InboxId));

        if (await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return ClaimConflict;

        Result projected = await AdvanceRemotePendingInboxWithinAsync(connection, transaction, row, root, ct).ConfigureAwait(false);

        return projected.IsFailure ? projected.Error : true;
    }

    private async Task<Result> AdvanceRemotePendingInboxWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        RemotePortalDelivery row, Guid root, CancellationToken ct)
    {
        if (row.Revision == long.MaxValue) return Exhausted;

        await using SqliteCommand advance = Command(connection, transaction, """
            UPDATE guild_inbox SET Revision=Revision+1 WHERE Id=$inbox AND Revision=$revision AND DeliveryStateCode=1
            """);

        Add(advance, "$inbox", Id(row.InboxId));

        Add(advance, "$revision", row.Revision);

        if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return ClaimConflict;

        var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, root, row.Recipient,
            GuildEventKind.MessageCommitted, row.InboxId, row.Revision + 1, row.EnvelopeId, time.GetUtcNow(), ct,
            GuildPortalEventSource.Inbox).ConfigureAwait(false);

        return projected.IsFailure ? projected.Error : Result.Success();
    }

    private static bool ClosedRemoteRecipient(GuildQuestDto quest) => quest.CancellationRequested
        || quest.State is QuestState.Completed or QuestState.Failed or QuestState.Rejected or QuestState.Canceled;
}
