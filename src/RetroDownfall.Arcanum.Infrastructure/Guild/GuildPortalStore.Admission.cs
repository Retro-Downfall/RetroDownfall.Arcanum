using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal async Task<Result<GuildMessageReceipt>> PublishWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, Guid questId, GuildPublishRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        Result<GuildPublishRequest> normalized = Normalize(request, caller.Kind == GuildCallerKind.HostReceipt);

        if (normalized.IsFailure) return normalized.Error;

        request = normalized.Value;

        Result<GuildQuestStored> found = await GuildQuestStore.ReadWithinAsync(connection, transaction, questId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildQuestStored source = found.Value;

        if (toolAdmission is not null)
        {
            if (toolAdmission.Kind != GuildLocalChildToolKind.SendAgentMessage || !ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                || questId != caller.Execution?.QuestId || request.Address.QuestId is not { } taskRecipientId) return Forbidden;

            Result admitted = await quests.AuthorizeTaskToolWithinAsync(connection, transaction, signets, toolAdmission,
                taskRecipientId, true, ct).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;
        }

        Result<GuildProducerIdentity> stamped = await AuthorizeProducerWithinAsync(connection, transaction, signets, caller, source, request, ct).ConfigureAwait(false);

        if (stamped.IsFailure) return stamped.Error;

        GuildProducerIdentity producer = stamped.Value;

        // The address is canonicalized before deduplication; current recipient eligibility is checked
        // only for a new acceptance, preserving the original recipient identity on retries.
        Result<Recipient> addressed = ResolveAddress(caller, source, request.Address);

        if (addressed.IsFailure) return addressed.Error;

        Recipient recipient = addressed.Value;

        if (recipient.QuestId is null)
        {
            Result<GuildSignetRecord> origin = await GuildSignetStore.ReadAsync(connection, transaction, source.Quest.SignetId, false, ct).ConfigureAwait(false);

            if (origin.IsFailure) return origin.Error;

            recipient = recipient with { Key = "operator:" + Id(origin.Value.Binding.OriginPrincipalId) };
        }

        request = request with { Address = recipient.Address };

        // A revision is a precondition for a new acceptance, not part of the logical message.
        // Host outbox retries may omit it while retaining the exact immutable receipt identity.
        byte[] intent = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request with { ExpectedRevision = null }, GuildJsonContext.Default.GuildPublishRequest));

        string producerKey = ProducerKey(producer);

        Guid streamId = Guid.NewGuid();

        long sequence = 0;

        await using (SqliteCommand stream = Command(connection, transaction, """
            SELECT StreamId,LastSequence FROM guild_portal_streams WHERE QuestId=$quest AND ProducerKindCode=$kind AND ProducerKey=$key
            """))
        {
            Add(stream, "$quest", Id(questId));

            Add(stream, "$kind", (int)producer.Kind);

            Add(stream, "$key", producerKey);

            await using SqliteDataReader reader = await stream.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                streamId = Guid.Parse(reader.GetString(0));

                sequence = reader.GetInt64(1);
            }
        }

        await using (SqliteCommand duplicate = Command(connection, transaction, """
            SELECT e.Id,e.IntentDigest,e.CreatedAtUtc,
                (SELECT EventSequence FROM guild_events p WHERE p.RootQuestId=e.RootQuestId AND p.EnvelopeId=e.Id AND p.EventKindCode=1 AND p.SourceKindCode=3 AND p.ObjectId=e.Id AND p.Revision=1)
            FROM guild_envelopes e WHERE e.StreamId=$stream AND e.ProducerRequestId=$request
            """))
        {
            Add(duplicate, "$stream", Id(streamId));

            Add(duplicate, "$request", Id(request.IdempotencyKey));

            await using SqliteDataReader reader = await duplicate.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetValue(1) is not byte[] prior || !CryptographicOperations.FixedTimeEquals(prior, intent)) return Conflict;

                if (reader.IsDBNull(3)) return Unavailable;

                Guid envelopeId = Guid.Parse(reader.GetString(0));

                return new GuildMessageReceipt(questId, envelopeId, 1, GuildDeliveryState.Pending, reader.GetInt64(3),
                    new(envelopeId, request.IdempotencyKey, true, UtcInstantText.Parse(reader.GetString(2)), 1));
            }
        }

        if (caller.Kind == GuildCallerKind.Run && (source.Quest.CancellationRequested
            || source.Quest.State is QuestState.Completed or QuestState.Failed or QuestState.Rejected or QuestState.Canceled))
            return new Error(ErrorCodes.Guild.Rejected, "The source Quest is closed to new run-authored Portal messages.");

        if (request.ExpectedRevision is { } expected && source.Quest.Revision != expected)
            return new Error(ErrorCodes.Guild.RevisionConflict, "The source Quest changed before publication.");

        if (recipient.QuestId is { } recipientId)
        {
            Result<GuildQuestStored> target = await GuildQuestStore.ReadWithinAsync(connection, transaction, recipientId, ct).ConfigureAwait(false);

            if (target.IsFailure) return target.Error;

            if (target.Value.Quest.RootQuestId != source.Quest.RootQuestId
                || caller.Kind != GuildCallerKind.Operator && request.Address.Kind == GuildCircleAddressKind.Child && target.Value.Quest.ParentQuestId != questId)
                return Forbidden;

            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, target.Value.Quest.SessionId, target.Value.Quest.CampaignId, ct).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            if (target.Value.Retired) return Forbidden;

            if (target.Value.Quest.CancellationRequested || target.Value.Quest.State is QuestState.Completed or QuestState.Failed or QuestState.Rejected or QuestState.Canceled)
                return new Error(ErrorCodes.Guild.Rejected, "The recipient Quest is closed to new Portal input.");

            Result<GuildGrant> eligible = await signets.ValidateCurrentGrantAsync(recipientId, ct).ConfigureAwait(false);

            if (eligible.IsFailure) return eligible.Error;
        }

        if (sequence == long.MaxValue) return Exhausted;

        DateTimeOffset now = time.GetUtcNow();

        Guid id = Guid.NewGuid();

        byte[] body = StrictUtf8.GetBytes(request.Body);

        Result<GuildDataRef> content;

        if (caller.Kind == GuildCallerKind.HostReceipt && request.Kind != GuildEnvelopeKind.QuestIssued && source.Quest.ResultRef is null)
        {
            Result<(string MimeType, string Body)> notice = await ReadHostNoticeWithinAsync(connection, transaction, source, request.Kind, ct).ConfigureAwait(false);

            if (notice.IsFailure) return notice.Error;

            if (notice.Value.MimeType != request.MimeType || notice.Value.Body != request.Body) return Conflict;

            content = await WriteBodyWithinAsync(connection, transaction, caller, source, id, request.MimeType, body, now, ct, toolAdmission).ConfigureAwait(false);
        }
        else
        {
            content = caller.Kind == GuildCallerKind.HostReceipt
                ? HostContent(source, request, body)
                : await WriteBodyWithinAsync(connection, transaction, caller, source, id, request.MimeType, body, now, ct, toolAdmission).ConfigureAwait(false);
        }

        if (content.IsFailure) return content.Error;

        GuildEnvelope envelope = new(id, producer, questId, source.Quest.RootQuestId,
            caller.Kind == GuildCallerKind.Run ? source.Quest.SignetRevision : null, source.Quest.SessionId, source.Quest.CampaignId,
            caller.Kind == GuildCallerKind.Operator ? GuildAuthorityKind.Operator
                : request.Address.Kind == GuildCircleAddressKind.Child ? GuildAuthorityKind.Parent : GuildAuthorityKind.Child,
            request.Kind, content.Value, sequence + 1, now, request.Address);

        string metadata = JsonSerializer.Serialize(envelope, GuildJsonContext.Default.GuildEnvelope);

        if (Encoding.UTF8.GetByteCount(metadata) > 16_384) return TooLarge;

        await using (SqliteCommand advance = Command(connection, transaction, """
            INSERT INTO guild_portal_streams(StreamId,QuestId,ProducerKindCode,ProducerKey,LastSequence)
            VALUES($stream,$quest,$kind,$key,1)
            ON CONFLICT(QuestId,ProducerKindCode,ProducerKey) DO UPDATE SET LastSequence=LastSequence+1
            WHERE LastSequence=$previous AND LastSequence<9223372036854775807
            """))
        {
            Add(advance, "$stream", Id(streamId));

            Add(advance, "$quest", Id(questId));

            Add(advance, "$kind", (int)producer.Kind);

            Add(advance, "$key", producerKey);

            Add(advance, "$previous", sequence);

            if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Exhausted;
        }

        await using (SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_envelopes(Id,StreamId,ProducerRequestId,IntentDigest,QuestId,RootQuestId,SessionId,CampaignId,KindCode,ContentId,SenderSequence,MetadataJson,CreatedAtUtc)
            VALUES($id,$stream,$request,$digest,$quest,$root,$session,$campaign,$kind,$content,$sequence,$metadata,$now);
            INSERT INTO guild_inbox(Id,EnvelopeId,RecipientKey,RecipientQuestId,StreamId,SenderSequence,DeliveryStateCode,Revision,CreatedAtUtc)
            VALUES($inbox,$id,$recipient,$recipientQuest,$stream,$sequence,1,1,$now);
            """))
        {
            Add(insert, "$id", Id(id));

            Add(insert, "$stream", Id(streamId));

            Add(insert, "$request", Id(request.IdempotencyKey));

            Add(insert, "$digest", intent);

            Add(insert, "$quest", Id(questId));

            Add(insert, "$root", Id(source.Quest.RootQuestId));

            Add(insert, "$session", Id(source.Quest.SessionId));

            Add(insert, "$campaign", Id(source.Quest.CampaignId));

            Add(insert, "$kind", (int)request.Kind);

            Add(insert, "$content", Id(content.Value.ContentId));

            Add(insert, "$sequence", sequence + 1);

            Add(insert, "$metadata", metadata);

            Add(insert, "$now", UtcInstantText.Format(now));

            Add(insert, "$inbox", Id(Guid.NewGuid()));

            Add(insert, "$recipient", recipient.Key);

            Add(insert, "$recipientQuest", Id(recipient.QuestId));

            if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 2) return Unavailable;
        }

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, source.Quest.RootQuestId,
            questId, GuildEventKind.MessageCommitted, id, 1, id, now, ct, GuildPortalEventSource.Envelope).ConfigureAwait(false);

        return projected.IsFailure ? projected.Error : new GuildMessageReceipt(questId, id, 1, GuildDeliveryState.Pending,
            projected.Value, new(id, request.IdempotencyKey, false, now, 1));
    }

    internal static async Task<Result<(string MimeType, string Body)>> ReadHostNoticeWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored source, GuildEnvelopeKind kind, CancellationToken ct)
    {
        GuildDataRef? reference = kind == GuildEnvelopeKind.QuestIssued ? source.Quest.GoalRef : source.Quest.ResultRef;

        if (reference is not null)
        {
            if (reference.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The receipt content was erased.");

            await using SqliteCommand content = Command(connection, transaction,
                "SELECT CASE WHEN typeof(Body)='blob' AND length(Body)<=65536 THEN Body ELSE NULL END FROM guild_content WHERE ContentId=$id");

            Add(content, "$id", Id(reference.ContentId));

            if (await content.ExecuteScalarAsync(ct).ConfigureAwait(false) is not byte[] bytes
                || bytes.Length != reference.ByteLength || Convert.ToHexString(SHA256.HashData(bytes)) != reference.Digest) return Unavailable;

            try
            {
                return (reference.MimeType, StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException)
            {
                return Unavailable;
            }
        }

        await using SqliteCommand receipt = Command(connection, transaction, """
            SELECT ResultId,Digest,OutcomeCode,CASE WHEN ReasonCode IS NULL OR length(CAST(ReasonCode AS BLOB))<=65536 THEN ReasonCode ELSE NULL END
            FROM guild_result_receipts WHERE QuestId=$quest AND ContentId IS NULL
            """);

        Add(receipt, "$quest", Id(source.Quest.Id));

        await using SqliteDataReader reader = await receipt.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetValue(1) is not byte[] digest || digest.Length != 32) return Unavailable;

        GuildResultReceipt typed = new(source.Quest.Id, Guid.Parse(reader.GetString(0)), Convert.ToHexString(digest), null,
            (QuestState)reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3));

        string body = JsonSerializer.Serialize(typed, GuildJsonContext.Default.GuildResultReceipt);

        return Encoding.UTF8.GetByteCount(body) <= GuildStore.MaximumContentBytes ? ("application/json", body) : TooLarge;
    }

    private static Result<GuildDataRef> HostContent(GuildQuestStored source, GuildPublishRequest request, byte[] body)
    {
        GuildDataRef? content = request.Kind == GuildEnvelopeKind.QuestIssued ? source.Quest.GoalRef : source.Quest.ResultRef;

        if (content is null) return Unavailable;

        if (content.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The source receipt body was erased.");

        return content.MimeType == request.MimeType && content.ByteLength == body.Length
            && content.Digest == Convert.ToHexString(SHA256.HashData(body)) ? content : Conflict;
    }

    private async Task<Result<GuildDataRef>> WriteBodyWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildQuestStored source, Guid envelopeId, string mimeType, byte[] body, DateTimeOffset now, CancellationToken ct,
        GuildChildToolAdmission? toolAdmission = null)
    {
        Guid contentId = Guid.NewGuid();

        if (source.Quest.GoalRef.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The source goal was erased before publication.");

        Result<ArtifactSensitivityLabel?> labelled = await ReadBoundedLabelWithinAsync(connection, transaction,
            source.Quest.GoalRef.ContentId, ct).ConfigureAwait(false);

        if (labelled.IsFailure) return labelled.Error;

        if (labelled.Value is { } goalLabel)
        {
            if (goalLabel.ArtifactRevision != (ulong)source.Quest.GoalRef.Revision || goalLabel.SessionId != source.Quest.SessionId
                || goalLabel.CampaignId != source.Quest.CampaignId || Convert.ToHexString(goalLabel.ArtifactContentDigest.Bytes) != source.Quest.GoalRef.Digest)
                return new Error(ErrorCodes.Covenant.IntegrityFailure, "The source goal sensitivity does not bind its immutable content.");

            Result consistent = await GuildContentLifecycle.ValidateArtifactAsync(connection, transaction, goalLabel, ct).ConfigureAwait(false);

            if (consistent.IsFailure) return consistent.Error;
        }
        else
        {
            await using SqliteCommand clean = Command(connection, transaction, """
                SELECT 1 FROM guild_content WHERE ContentId=$id AND SensitivityCode=0 AND ProvenanceModeCode=0
                    AND ExactGenerationIds IS NULL AND GenerationBloom IS NULL
                """);

            Add(clean, "$id", Id(source.Quest.GoalRef.ContentId));

            if (await clean.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return new Error(ErrorCodes.Covenant.IntegrityFailure, "The source goal is missing its sensitivity evidence.");
        }

        Result<ArtifactSensitivityLabel?> resolved = Result<ArtifactSensitivityLabel?>.Success(null);

        if (caller.Kind != GuildCallerKind.HostReceipt)
        {
            if (policy is IGuildPortalDisclosurePolicy disclosure)
            {
                resolved = await disclosure.ResolvePublicationSensitivityAsync(caller, source, connection, transaction, ct).ConfigureAwait(false);

                if (resolved.IsFailure) return resolved.Error;
            }
            else
            {
                // The exact live task admission above materialized every accepted provider source.
                // That boundary refuses protected content; it is not an operator publication bypass.
                if (toolAdmission is null || !toolAdmission.Registry.Owns(toolAdmission)
                    || toolAdmission.Kind != GuildLocalChildToolKind.SendAgentMessage || labelled.Value is not null) return Unavailable;
            }
        }

        ArtifactSensitivityLabel? label = resolved.Value ?? labelled.Value;

        GenerationProvenance provenance = (labelled.Value?.Provenance ?? GenerationProvenance.CreateExact([]))
            .Merge(resolved.Value?.Provenance ?? GenerationProvenance.CreateExact([]));

        ContentSensitivity taint = labelled.Value?.Sensitivity == ContentSensitivity.CovenantDerived || resolved.Value?.Sensitivity == ContentSensitivity.CovenantDerived
            ? ContentSensitivity.CovenantDerived : ContentSensitivity.None;

        DerivedArtifactWrite sensitivity = new(SensitiveArtifactKind.GuildContent, contentId, source.Quest.SessionId, source.Quest.CampaignId,
            label?.TurnId, 1, new CovenantDigest(SHA256.HashData(body)), taint,
            provenance, label?.ProducingPlanDigest, label?.ProducingAdmissionDigest,
            label?.ProducingMaintenanceReceiptDigest);

        return await GuildStore.WriteContentWithinAsync(connection, transaction, new(contentId, envelopeId, GuildContentKind.CircleBody,
            1, source.Quest.SessionId, source.Quest.CampaignId, mimeType, body, sensitivity), now, ct).ConfigureAwait(false);
    }

    private static Result<Recipient> ResolveAddress(GuildCallerContext caller, GuildQuestStored source, GuildCircleAddress address)
    {
        if (caller.Kind == GuildCallerKind.Operator)
            return address.Kind == GuildCircleAddressKind.Child && address.QuestId == source.Quest.Id
                ? new Recipient("quest:" + Id(source.Quest.Id), source.Quest.Id, address) : Forbidden;

        if (caller.Kind == GuildCallerKind.HostReceipt && (source.Quest.ParentQuestId is null
                ? address.Kind != GuildCircleAddressKind.Operator
                : address.Kind != GuildCircleAddressKind.Parent)) return Forbidden;

        if (address.Kind == GuildCircleAddressKind.Parent && source.Quest.ParentQuestId is { } parent
            && (address.QuestId is null || address.QuestId == parent))
            return new Recipient("quest:" + Id(parent), parent, address with { QuestId = parent });

        if (address.Kind == GuildCircleAddressKind.Child && address.QuestId is { } child && child != source.Quest.Id)
            return new Recipient("quest:" + Id(child), child, address);

        // The immutable Signet origin is resolved before any inbox row is accepted.
        if (address.Kind == GuildCircleAddressKind.Operator && address.QuestId is null)
            return new Recipient("operator:" + Id(source.Quest.SignetId), null, address);

        return Forbidden;
    }

    private sealed record Recipient(string Key, Guid? QuestId, GuildCircleAddress Address);
}
