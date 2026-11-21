using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal Task<Result<GuildPortalMessageDeliveryReceipt>> AccessInboundAsync(GuildCallerContext caller, GuildInboundBinding located,
        GuildPortalMessageReceiptQuery query, GuildPortalMessageRequest? message, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildPortalMessageDeliveryReceipt>(located.QuestId,
            async (connection, transaction, signets, token) =>
            {
                if (caller.Kind != GuildCallerKind.Peer || caller.PeerId != located.PeerId
                    || caller.PeerGrantRevision != located.GrantRevision || caller.InvocationId != query.MessageId) return MissingInbound;

                var binding = await GuildInboundTaskStore.ReadWithinAsync(connection, transaction, located.PeerId, query.TaskId, null, token)
                    .ConfigureAwait(false);

                if (binding.IsFailure) return binding.Error;

                if (binding.Value != located || located.ContextId != query.ContextId) return MissingInbound;

                var found = await GuildQuestStore.ReadWithinAsync(connection, transaction, located.QuestId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                GuildQuestStored recipient = found.Value;

                if (recipient.Quest.RootQuestId != located.QuestId) return MissingInbound;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, recipient, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                var prior = await ReadInboundReceiptWithinAsync(connection, transaction, located, query, token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } existing) return existing;

                if (message is null) return MissingInbound;

                if (recipient.Quest.CancellationRequested || recipient.Quest.State is QuestState.Completed or QuestState.Failed
                    or QuestState.Rejected or QuestState.Canceled) return ClosedRecipient;

                var eligible = await signets.ValidateCurrentGrantAsync(located.QuestId, token).ConfigureAwait(false);

                if (eligible.IsFailure) return eligible.Error;

                var published = await PublishInboundWithinAsync(connection, transaction, recipient.Quest, located, message, token)
                    .ConfigureAwait(false);

                return published.IsSuccess && policy is GuildLocalHostPolicy host && !host.Authenticates(caller)
                    ? Result<GuildPortalMessageDeliveryReceipt>.Failure(MissingInbound) : published;
            }, ct);

    private async Task<Result<GuildPortalMessageDeliveryReceipt>> PublishInboundWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestDto recipient, GuildInboundBinding binding, GuildPortalMessageRequest message,
        CancellationToken ct)
    {
        await using (SqliteCommand order = Command(connection, transaction, """
            SELECT SourceSequence FROM guild_inbound_portal_messages
            WHERE QuestId=$quest AND SourceStreamId=$stream ORDER BY SourceSequence DESC LIMIT 1
            """))
        {
            Add(order, "$quest", Id(recipient.Id));

            Add(order, "$stream", Id(message.StreamId));

            object? latest = await order.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (latest is not null && (latest is not long sequence || sequence < 1 || sequence >= message.Sequence))
                return new Error(ErrorCodes.Guild.RevisionConflict, "The informational message is out of sender order.");
        }

        GuildProducerIdentity producer = new(GuildProducerKind.A2APeer, null, null, null, null, null, null, null,
            new(binding.PeerId, binding.GrantRevision, binding.AuthorityEpoch, message.StreamId));

        var advanced = await AdvanceInboundStreamWithinAsync(connection, transaction, recipient.Id, producer, ct).ConfigureAwait(false);

        if (advanced.IsFailure) return advanced.Error;

        (Guid streamId, long sequenceNumber) = advanced.Value;

        DateTimeOffset now = time.GetUtcNow();

        Guid envelopeId = Guid.NewGuid();

        Guid inboxId = Guid.NewGuid();

        Guid contentId = Guid.NewGuid();

        byte[] body = StrictUtf8.GetBytes(message.Body);

        DerivedArtifactWrite sensitivity = new(SensitiveArtifactKind.GuildContent, contentId, recipient.SessionId,
            recipient.CampaignId, null, 1, new CovenantDigest(SHA256.HashData(body)), ContentSensitivity.None,
            GenerationProvenance.CreateExact([]));

        var content = await GuildStore.WriteContentWithinAsync(connection, transaction,
            new(contentId, envelopeId, GuildContentKind.CircleBody, 1, recipient.SessionId, recipient.CampaignId,
                message.MimeType, body, sensitivity), now, ct).ConfigureAwait(false);

        if (content.IsFailure) return content.Error;

        GuildEnvelope envelope = new(envelopeId, producer, recipient.Id, recipient.Id, null, recipient.SessionId,
            recipient.CampaignId, GuildAuthorityKind.Parent, GuildEnvelopeKind.Progress, content.Value, sequenceNumber, now,
            new(GuildCircleAddressKind.Child, recipient.Id, null, null));

        string metadata = JsonSerializer.Serialize(envelope, GuildJsonContext.Default.GuildEnvelope);

        if (StrictUtf8.GetByteCount(metadata) > 16_384) return TooLarge;

        await using SqliteCommand write = Command(connection, transaction, """
            INSERT INTO guild_envelopes(Id,StreamId,ProducerRequestId,IntentDigest,QuestId,RootQuestId,SessionId,CampaignId,
                KindCode,ContentId,SenderSequence,MetadataJson,CreatedAtUtc)
            VALUES($envelope,$stream,$message,$digest,$quest,$quest,$session,$campaign,$kind,$content,$sequence,$metadata,$now);
            INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
            VALUES($inbox,$envelope,$recipient,$quest,$stream,$sequence,1,1,$now);
            INSERT INTO guild_inbound_portal_messages(QuestId,MessageId,SourceStreamId,SourceSequence,InboxId,EnvelopeId)
            VALUES($quest,$message,$sourceStream,$sourceSequence,$inbox,$envelope);
            """);

        Add(write, "$quest", Id(recipient.Id));

        Add(write, "$session", Id(recipient.SessionId));

        Add(write, "$campaign", Id(recipient.CampaignId));

        Add(write, "$stream", Id(streamId));

        Add(write, "$message", Id(message.MessageId));

        Add(write, "$sourceStream", Id(message.StreamId));

        Add(write, "$sourceSequence", message.Sequence);

        Add(write, "$digest", Convert.FromHexString(message.IntentDigest));

        Add(write, "$kind", (int)envelope.Kind);

        Add(write, "$metadata", metadata);

        Add(write, "$sequence", sequenceNumber);

        Add(write, "$content", Id(contentId));

        Add(write, "$envelope", Id(envelopeId));

        Add(write, "$inbox", Id(inboxId));

        Add(write, "$recipient", "quest:" + Id(recipient.Id));

        Add(write, "$now", UtcInstantText.Format(now));

        if (await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 3) return Unavailable;

        var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, recipient.Id, recipient.Id,
            GuildEventKind.MessageCommitted, envelopeId, 1, envelopeId, now, ct, GuildPortalEventSource.Envelope).ConfigureAwait(false);

        return projected.IsFailure ? projected.Error
            : new GuildPortalMessageDeliveryReceipt(message.TaskId, message.ContextId, message.MessageId, message.IntentDigest,
                inboxId, GuildDeliveryState.Pending, 1, null);
    }

    private static async Task<Result<(Guid StreamId, long Sequence)>> AdvanceInboundStreamWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid quest, GuildProducerIdentity producer, CancellationToken ct)
    {
        await using SqliteCommand advance = Command(connection, transaction, """
            INSERT INTO guild_portal_streams(StreamId,QuestId,ProducerKindCode,ProducerKey,LastSequence)
            VALUES($stream,$quest,$kind,$key,1)
            ON CONFLICT(QuestId,ProducerKindCode,ProducerKey) DO UPDATE SET LastSequence=LastSequence+1
            WHERE typeof(LastSequence)='integer' AND LastSequence BETWEEN 0 AND 9223372036854775806
            RETURNING CASE WHEN typeof(StreamId)='text' AND length(CAST(StreamId AS BLOB))=36 THEN StreamId END,LastSequence
            """);

        Add(advance, "$stream", Id(Guid.NewGuid()));

        Add(advance, "$quest", Id(quest));

        Add(advance, "$kind", (int)producer.Kind);

        Add(advance, "$key", ProducerKey(producer));

        await using SqliteDataReader reader = await advance.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Exhausted;

        return !reader.IsDBNull(0) && Guid.TryParseExact(reader.GetString(0), "D", out Guid stream) && stream != Guid.Empty
            && reader.GetValue(1) is long sequence && sequence > 0 ? (stream, sequence) : Unavailable;
    }

    private static async Task<Result<GuildPortalMessageDeliveryReceipt?>> ReadInboundReceiptWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildInboundBinding binding, GuildPortalMessageReceiptQuery query, CancellationToken ct)
    {
        await using SqliteCommand read = Command(connection, transaction, """
            SELECT CASE WHEN typeof(i.Id)='text' AND length(CAST(i.Id AS BLOB))=36 THEN i.Id END,
                i.DeliveryStateCode,i.Revision,
                CASE WHEN typeof(e.IntentDigest)='blob' AND length(e.IntentDigest)=32 THEN hex(e.IntentDigest) END,
                CASE WHEN typeof(r.ApplicationReceiptId)='text' AND length(CAST(r.ApplicationReceiptId AS BLOB))=36
                    AND r.Digest=e.IntentDigest AND (
                        (i.DeliveryStateCode=2 AND r.EffectKindCode=0) OR (i.DeliveryStateCode=3 AND r.EffectKindCode=3))
                    THEN r.ApplicationReceiptId END,
                r.InboxId IS NOT NULL
            FROM guild_inbound_portal_messages m
            LEFT JOIN guild_inbox i ON i.Id=m.InboxId AND i.EnvelopeId=m.EnvelopeId AND i.RecipientQuestId=m.QuestId
                AND i.RecipientKey=('quest:' || m.QuestId)
            LEFT JOIN guild_envelopes e ON e.Id=i.EnvelopeId AND e.QuestId=m.QuestId AND e.RootQuestId=m.QuestId
                AND e.ProducerRequestId=m.MessageId AND e.KindCode=1 AND EXISTS (
                    SELECT 1 FROM guild_portal_streams s WHERE s.StreamId=e.StreamId AND s.QuestId=e.QuestId
                      AND s.ProducerKindCode=3 AND s.ProducerKey=($peer || ':' || $epoch || ':' || m.SourceStreamId))
            LEFT JOIN guild_portal_receipts r ON r.InboxId=i.Id
            WHERE m.QuestId=$quest AND m.MessageId=$message
            """);

        Add(read, "$quest", Id(binding.QuestId));

        Add(read, "$peer", binding.PeerId);

        Add(read, "$epoch", Id(binding.AuthorityEpoch));

        Add(read, "$message", Id(query.MessageId));

        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildPortalMessageDeliveryReceipt?>.Success(null);

        if (reader.IsDBNull(0) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid inbox) || inbox == Guid.Empty
            || reader.GetValue(1) is not long phase || phase is < 1 or > 3
            || reader.GetValue(2) is not long revision || revision < 1 || reader.IsDBNull(3)
            || (phase == 1 && reader.GetBoolean(5))) return Unavailable;

        if (reader.GetString(3) != query.IntentDigest) return Conflict;

        Guid? applied = null;

        if ((GuildDeliveryState)phase is GuildDeliveryState.Applied or GuildDeliveryState.Withheld)
        {
            if (reader.IsDBNull(4) || !Guid.TryParseExact(reader.GetString(4), "D", out Guid receipt) || receipt == Guid.Empty)
                return Unavailable;

            if ((GuildDeliveryState)phase == GuildDeliveryState.Applied) applied = receipt;
        }

        return new GuildPortalMessageDeliveryReceipt(query.TaskId, query.ContextId, query.MessageId, query.IntentDigest,
            inbox, (GuildDeliveryState)phase, revision, applied);
    }

    private static Error MissingInbound => new(ErrorCodes.Guild.NotFound, "The peer task or receipt is unavailable.");
}
