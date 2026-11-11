using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Host policy for current protected disclosure; unavailable composition never implies permission.</summary>
internal interface IGuildPortalDisclosurePolicy
{
    string VisibilityEpoch { get; }

    Task<Result<ArtifactSensitivityLabel?>> ResolvePublicationSensitivityAsync(GuildCallerContext caller, GuildQuestStored source,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);

    Task<Result> AuthorizeContentAsync(GuildCallerContext caller, ArtifactSensitivityLabel label,
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);
}

internal sealed partial class GuildPortalStore
{
    private async Task<Result<GuildProducerIdentity>> AuthorizeProducerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildQuestStored source, GuildPublishRequest request, CancellationToken ct)
    {
        if (caller.InstallationGeneration != policy.InstallationGeneration || source.Issuance.InstallationGeneration != policy.InstallationGeneration
            || source.Retired) return Forbidden;

        Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, source.Quest.SessionId, source.Quest.CampaignId, ct).ConfigureAwait(false);

        if (owner.IsFailure) return owner.Error;

        if (caller.Kind == GuildCallerKind.Run)
        {
            if (caller.Execution is not { } execution || execution.QuestId != source.Quest.Id || execution.AgentId != source.Quest.AgentId
                || execution.RunId != source.Quest.RunId || execution.SignetId != source.Quest.SignetId || execution.RootQuestId != source.Quest.RootQuestId
                || request.Kind is not (GuildEnvelopeKind.Progress or GuildEnvelopeKind.Escalation)) return Forbidden;

            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, source, ct).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            Result<GuildGrant> live = await signets.VerifyAsync(execution, ct).ConfigureAwait(false);

            if (live.IsFailure) return live.Error;

            return new GuildProducerIdentity(GuildProducerKind.Run, execution.AgentId, execution.RunId, execution.QuestId, null, null, null, null);
        }

        if (caller.Kind == GuildCallerKind.Operator)
        {
            if (request.Kind != GuildEnvelopeKind.Intervention) return Forbidden;

            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, source, ct).ConfigureAwait(false);

            return visible.IsFailure ? visible.Error : new GuildProducerIdentity(GuildProducerKind.Operator, null, null, null,
                Id(caller.PrincipalId!.Value), null, null, null);
        }

        if (caller.Kind != GuildCallerKind.HostReceipt || caller.PrincipalId != source.Quest.Id || caller.ReceiptId != request.IdempotencyKey)
            return Forbidden;

        Result<GuildSignetRecord> sourceSignet = await GuildSignetStore.ReadAsync(connection, transaction, source.Quest.SignetId, false, ct).ConfigureAwait(false);

        if (sourceSignet.IsFailure) return sourceSignet.Error;

        Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure) return epoch.Error;

        if (sourceSignet.Value.RestoredInert || sourceSignet.Value.AuthorityEpoch != epoch.Value)
            return new Error(ErrorCodes.Guild.AuthorityRevoked, "Retired authority cannot publish a new Portal receipt effect.");

        GuildReceiptSourceKind? receiptKind = caller.ReceiptSource switch
        {
            "questIssue" => GuildReceiptSourceKind.QuestIssue,
            "result" => GuildReceiptSourceKind.Result,
            "settlement" => GuildReceiptSourceKind.Settlement,
            _ => null,
        };

        bool valid = receiptKind switch
        {
            GuildReceiptSourceKind.QuestIssue => request.Kind == GuildEnvelopeKind.QuestIssued && caller.ReceiptId == source.Quest.Id,
            GuildReceiptSourceKind.Result => request.Kind == GuildEnvelopeKind.Result && caller.ReceiptId == source.Quest.ResultReceiptId,
            GuildReceiptSourceKind.Settlement => request.Kind == GuildEnvelopeKind.Settle && caller.ReceiptId == source.Quest.ResultReceiptId
                && source.Quest.AccountingState == GuildAccountingState.Settled,
            _ => false,
        };

        if (!valid) return Forbidden;

        // The receipt id is proved by the immutable owning domain row, not by the host tag alone.
        await using SqliteCommand receipt = Command(connection, transaction, receiptKind == GuildReceiptSourceKind.QuestIssue
            ? "SELECT 1 FROM guild_quest_start_intents WHERE QuestId=$quest"
            : "SELECT 1 FROM guild_result_receipts WHERE QuestId=$quest AND ResultId=$receipt");

        Add(receipt, "$quest", Id(source.Quest.Id));

        if (receiptKind != GuildReceiptSourceKind.QuestIssue) Add(receipt, "$receipt", Id(caller.ReceiptId));

        if (await receipt.ExecuteScalarAsync(ct).ConfigureAwait(false) is null) return Forbidden;

        return new GuildProducerIdentity(GuildProducerKind.HostReceipt, null, null, null, null,
            source.Quest.Id, receiptKind, caller.ReceiptId);
    }

    internal async Task<Result> RequireReaderWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildEnvelope envelope, CancellationToken ct)
    {
        if (caller is null || caller.Kind is not (GuildCallerKind.Run or GuildCallerKind.Operator)
            || caller.InstallationGeneration != policy.InstallationGeneration) return Forbidden;

        Result<GuildQuestStored> source = await GuildQuestStore.ReadWithinAsync(connection, transaction, envelope.QuestId, ct).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        if (source.Value.Quest.RootQuestId != envelope.RootQuestId
            || envelope.Producer.Kind == GuildProducerKind.Run && (envelope.Producer.AgentId != source.Value.Quest.AgentId
                || envelope.Producer.RunId != source.Value.Quest.RunId || envelope.Producer.QuestId != source.Value.Quest.Id)) return Unavailable;

        Result sourceVisible = caller.Kind == GuildCallerKind.Run && envelope.Address.Kind == GuildCircleAddressKind.Cohort
            ? source.Value.Retired || source.Value.Issuance.InstallationGeneration != policy.InstallationGeneration
                ? Result.Failure(Forbidden)
                : await GuildStore.RequireOwnerWithinAsync(connection, transaction, source.Value.Quest.SessionId,
                    source.Value.Quest.CampaignId, ct).ConfigureAwait(false)
            : await quests.RequireVisibleAsync(connection, transaction, caller, source.Value, ct).ConfigureAwait(false);

        if (sourceVisible.IsFailure) return sourceVisible;

        if (caller.Kind == GuildCallerKind.Operator) return Result.Success();

        GuildExecutionHandle execution = caller.Execution!;

        Result<GuildGrant> live = await signets.VerifyAsync(execution, ct).ConfigureAwait(false);

        if (live.IsFailure) return live.Error;

        Result<GuildQuestStored> recipient = await GuildQuestStore.ReadWithinAsync(connection, transaction, execution.QuestId, ct).ConfigureAwait(false);

        if (recipient.IsFailure) return recipient.Error;

        if (execution.RootQuestId != envelope.RootQuestId || recipient.Value.Quest.SignetId != execution.SignetId
            || recipient.Value.Quest.AgentId != execution.AgentId || recipient.Value.Quest.RunId != execution.RunId) return Forbidden;

        Result recipientVisible = await quests.RequireVisibleAsync(connection, transaction, caller, recipient.Value, ct).ConfigureAwait(false);

        if (recipientVisible.IsFailure) return recipientVisible;

        bool relation = envelope.Producer.Kind == GuildProducerKind.Operator
            ? envelope.QuestId == recipient.Value.Quest.Id && envelope.Address.Kind == GuildCircleAddressKind.Child
            : envelope.Address.Kind switch
            {
                GuildCircleAddressKind.Parent => source.Value.Quest.ParentQuestId == recipient.Value.Quest.Id,
                GuildCircleAddressKind.Child => recipient.Value.Quest.ParentQuestId == source.Value.Quest.Id,
                GuildCircleAddressKind.Cohort => await HasCurrentCohortAudienceWithinAsync(connection, transaction,
                    envelope, recipient.Value.Quest.Id, ct).ConfigureAwait(false),
                _ => false,
            };

        if (!relation || envelope.Address.Kind != GuildCircleAddressKind.Cohort
            && envelope.Address.QuestId != recipient.Value.Quest.Id) return Forbidden;

        if (envelope.Address.Kind == GuildCircleAddressKind.Cohort)
        {
            Result<GuildGrant> sourceGrant = await signets.ValidateCurrentGrantAsync(envelope.QuestId, ct).ConfigureAwait(false);

            if (sourceGrant.IsFailure) return sourceGrant.Error;
        }

        await using SqliteCommand inbox = Command(connection, transaction,
            "SELECT 1 FROM guild_inbox WHERE EnvelopeId=$envelope AND RecipientQuestId=$quest AND RecipientKey=$key");

        Add(inbox, "$envelope", Id(envelope.Id));

        Add(inbox, "$quest", Id(execution.QuestId));

        Add(inbox, "$key", "quest:" + Id(execution.QuestId));

        return await inbox.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null ? Result.Success() : Forbidden;
    }

    internal async Task<Result<byte[]>> ReadBodyWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildEnvelope envelope, CancellationToken ct)
    {
        // Treat even this internal metadata as a lookup hint and reload its immutable stored binding.
        Result<GuildEnvelope> stored = await ReadEnvelopeWithinAsync(connection, transaction, envelope.Id, ct).ConfigureAwait(false);

        if (stored.IsFailure) return stored.Error;

        envelope = stored.Value;

        Result visible = await RequireReaderWithinAsync(connection, transaction, signets, caller, envelope, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        if (envelope.ContentRef.ContentErased) return new Error(ErrorCodes.Guild.ContentErased, "The Portal body was erased.");

        Result<ArtifactSensitivityLabel?> found = await ReadBoundedLabelWithinAsync(connection, transaction,
            envelope.ContentRef.ContentId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        ArtifactSensitivityLabel? label = found.Value;

        if (label is not null)
        {
            if (label.ArtifactRevision != (ulong)envelope.ContentRef.Revision || label.SessionId != envelope.SessionId
                || label.CampaignId != envelope.CampaignId || Convert.ToHexString(label.ArtifactContentDigest.Bytes) != envelope.ContentRef.Digest)
                return Unavailable;

            Result available = await GuildStorageAvailability.CheckCovenantAsync(connection, transaction, ct).ConfigureAwait(false);

            if (available.IsFailure) return available.Error;

            if (policy is not IGuildPortalDisclosurePolicy disclosure) return Unavailable;

            Result authorized = await disclosure.AuthorizeContentAsync(caller, label, connection, transaction, ct).ConfigureAwait(false);

            if (authorized.IsFailure) return authorized.Error;

            Result consistent = await GuildContentLifecycle.ValidateArtifactAsync(connection, transaction, label, ct).ConfigureAwait(false);

            if (consistent.IsFailure) return consistent.Error;
        }

        await using SqliteCommand content = Command(connection, transaction, """
            SELECT SessionId,CampaignId,ContentKindCode,Revision,Digest,MimeType,length(Body),SensitivityCode,ProvenanceModeCode,
                CASE WHEN typeof(Body)='blob' AND length(Body)<=65536 THEN Body ELSE NULL END,
                CASE WHEN (SessionId IS NULL OR (typeof(SessionId)='text' AND length(CAST(SessionId AS BLOB))=36))
                    AND (CampaignId IS NULL OR (typeof(CampaignId)='text' AND length(CAST(CampaignId AS BLOB))=36))
                    AND typeof(ContentKindCode)='integer' AND ContentKindCode BETWEEN 0 AND 10
                    AND typeof(Revision)='integer' AND Revision>=1
                    AND typeof(Digest)='blob' AND length(Digest)=32
                    AND typeof(MimeType)='text' AND length(CAST(MimeType AS BLOB)) BETWEEN 1 AND 65536
                    AND typeof(Body)='blob' AND length(Body)<=65536
                    AND typeof(SensitivityCode)='integer' AND SensitivityCode IN(0,1)
                    AND typeof(ProvenanceModeCode)='integer' AND ProvenanceModeCode IN(0,1,2)
                    THEN 1 ELSE 0 END
            FROM guild_content WHERE ContentId=$id
            """);

        Add(content, "$id", Id(envelope.ContentRef.ContentId));

        await using SqliteDataReader reader = await content.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(10) != 1) return Unavailable;

        if ((reader.IsDBNull(0) ? null : reader.GetString(0)) != Id(envelope.SessionId)
            || (reader.IsDBNull(1) ? null : reader.GetString(1)) != Id(envelope.CampaignId)
            || reader.GetInt32(2) != (int)envelope.ContentRef.Kind || reader.GetInt64(3) != envelope.ContentRef.Revision
            || reader.GetValue(4) is not byte[] digest || digest.Length != 32 || Convert.ToHexString(digest) != envelope.ContentRef.Digest
            || reader.GetString(5) != envelope.ContentRef.MimeType || reader.GetInt64(6) != envelope.ContentRef.ByteLength
            || reader.GetInt32(7) != (int)(label?.Sensitivity ?? ContentSensitivity.None)
            || label is null && reader.GetInt32(8) != 0 || reader.GetValue(9) is not byte[] body
            || !SHA256.HashData(body).AsSpan().SequenceEqual(digest)) return Unavailable;

        return body;
    }

    private static async Task<Result<ArtifactSensitivityLabel?>> ReadBoundedLabelWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid contentId, CancellationToken ct)
    {
        // Bound every variable label column before the sensitivity ledger materializes it.
        await using (SqliteCommand bounds = Command(connection, transaction, """
            SELECT CASE WHEN length(CAST(LabelId AS BLOB))>36 OR COALESCE(length(CAST(SessionId AS BLOB)),0)>36
                OR COALESCE(length(CAST(CampaignId AS BLOB)),0)>36 OR COALESCE(length(CAST(TurnId AS BLOB)),0)>36
                OR length(CAST(CreatedAtUtc AS BLOB))>64 OR length(ArtifactContentDigest)>32
                OR COALESCE(length(ExactGenerationIds),0)>128 OR COALESCE(length(GenerationBloom),0)>32
                OR COALESCE(length(ProducingPlanDigest),0)>32 OR COALESCE(length(ProducingAdmissionDigest),0)>32
                OR COALESCE(length(ProducingMaintenanceReceiptDigest),0)>32 THEN 1 ELSE 0 END
            FROM artifact_sensitivity WHERE ArtifactKindCode=17 AND ArtifactId=$id
            """))
        {
            Add(bounds, "$id", Id(contentId));

            if (Equals(1L, await bounds.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Unavailable;
        }

        return await ArtifactSensitivityLedger.ReadLabelWithinAsync(connection, transaction,
            SensitiveArtifactKind.GuildContent, contentId, ct).ConfigureAwait(false);
    }
}
