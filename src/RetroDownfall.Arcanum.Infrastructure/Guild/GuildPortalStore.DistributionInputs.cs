using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildPortalStore
{
    internal async Task<Result<string>> ReadInputPromptWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildEnvelope envelope, CancellationToken ct)
    {
        var stored = await ReadEnvelopeWithinAsync(connection, transaction, envelope.Id, ct).ConfigureAwait(false);

        if (stored.IsFailure) return stored.Error;

        envelope = stored.Value;

        var body = await ReadBodyWithinAsync(connection, transaction, signets, caller, envelope, ct).ConfigureAwait(false);

        if (body.IsFailure) return body.Error;

        var relationship = await InputRelationshipWithinAsync(connection, transaction, envelope, ct).ConfigureAwait(false);

        if (relationship.IsFailure) return relationship.Error;

        string raw;

        try { raw = StrictUtf8.GetString(body.Value); }
        catch (DecoderFallbackException) { return Unavailable; }

        StringBuilder prompt = new(GuildPromptBuilder.Build(relationship.Value, envelope.Kind, envelope.Id, raw));

        var bound = await ReadDistributionBindingWithinAsync(connection, transaction, envelope.Id, ct).ConfigureAwait(false);

        if (bound.IsFailure) return bound.Error;

        if (bound.Value is not { } notice) return prompt.ToString();

        if (envelope.Kind != GuildEnvelopeKind.Progress || envelope.Address.Kind != GuildCircleAddressKind.Cohort
            || envelope.Address.CohortId != notice.CohortId
            || !body.Value.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(notice, GuildJsonContext.Default.GuildCohortProgress))) return Unavailable;

        var cohort = await GuildCohortStore.ReadWithinAsync(connection, transaction, notice.CohortId, ct).ConfigureAwait(false);

        if (cohort.IsFailure) return cohort.Error;

        if (cohort.Value.Cohort.RootQuestId != envelope.RootQuestId) return Forbidden;

        Result materialized = notice.Type switch
        {
            "offer.posted" or "offer.withdrawn" => await AppendOfferInputWithinAsync(connection, transaction, caller, envelope, notice, prompt, ct).ConfigureAwait(false),
            "bid.submitted" or "bid.withdrawn" => await AppendBidInputWithinAsync(connection, transaction, caller, envelope, notice, prompt, ct).ConfigureAwait(false),
            "note.appended" => await AppendNoteInputWithinAsync(connection, transaction, caller, envelope, notice, prompt, ct).ConfigureAwait(false),
            "work.result" => await AppendAwardResultInputWithinAsync(connection, transaction, caller, envelope, notice, prompt, ct).ConfigureAwait(false),
            "work.awarded" => await AppendAwardInputWithinAsync(connection, transaction, caller, envelope, notice, prompt, ct).ConfigureAwait(false),
            _ => Unavailable,
        };

        if (materialized.IsFailure) return materialized.Error;

        return StrictUtf8.GetByteCount(prompt.ToString()) <= PortalPageBytes ? prompt.ToString() : TooLarge;
    }

    private static Task<Result<string>> ReadDistributionBodyWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildDataRef reference, CancellationToken ct) => reference.ContentErased
            ? Task.FromResult(Result<string>.Failure(new(ErrorCodes.Guild.ContentErased, "The referenced distribution content was erased.")))
            : GuildQuestStore.ReadProviderContentWithinAsync(connection, transaction, reference, ct);

    private static async Task<Result<GuildAuthorityKind>> InputRelationshipWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildEnvelope envelope, CancellationToken ct)
    {
        GuildAuthorityKind relationship;

        if (envelope.Producer.Kind == GuildProducerKind.Operator) relationship = GuildAuthorityKind.Operator;
        else if (envelope.Address.Kind == GuildCircleAddressKind.Child) relationship = GuildAuthorityKind.Parent;
        else if (envelope.Address.Kind == GuildCircleAddressKind.Parent) relationship = GuildAuthorityKind.Child;
        else if (envelope.Address.Kind == GuildCircleAddressKind.Cohort && envelope.Address.CohortId is { } cohort)
        {
            await using var issuer = Command(connection, transaction,
                "SELECT CASE WHEN length(CAST(IssuerQuestId AS BLOB))=36 THEN IssuerQuestId END FROM guild_cohorts WHERE Id=$cohort");

            Add(issuer, "$cohort", Id(cohort));

            if (await issuer.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string source || !Guid.TryParseExact(source, "D", out Guid creator)) return Unavailable;

            relationship = creator == envelope.QuestId ? GuildAuthorityKind.Parent : GuildAuthorityKind.Peer;
        }
        else return Unavailable;

        return relationship == envelope.Authority ? relationship : Unavailable;
    }

    private static async Task<Result<GuildCohortProgress?>> ReadDistributionBindingWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid envelopeId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT CASE WHEN typeof(CohortId)='text' AND length(CAST(CohortId AS BLOB))=36 THEN CohortId END,
                TypeCode,CASE WHEN typeof(ObjectId)='text' AND length(CAST(ObjectId AS BLOB))=36 THEN ObjectId END,ObjectSequence
            FROM guild_distribution_notifications WHERE EnvelopeId=$envelope
            """);

        Add(command, "$envelope", Id(envelopeId));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildCohortProgress?>.Success(null);

        if (reader.IsDBNull(0) || reader.IsDBNull(2) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid cohort) || cohort == Guid.Empty
            || !Guid.TryParseExact(reader.GetString(2), "D", out Guid item) || item == Guid.Empty
            || reader.GetValue(1) is not long code || code is < 1 or > 7 || reader.GetValue(3) is not long sequence || sequence < 1) return Unavailable;

        return new GuildCohortProgress(DistributionType((int)code), cohort, item, sequence);
    }

    private static int DistributionTypeCode(string type) => type switch
    {
        "offer.posted" => 1, "bid.submitted" => 2, "bid.withdrawn" => 3,
        "offer.withdrawn" => 4, "work.awarded" => 5, "note.appended" => 6, "work.result" => 7, _ => 0,
    };

    private static string DistributionType(int code) => code switch
    {
        1 => "offer.posted", 2 => "bid.submitted", 3 => "bid.withdrawn",
        4 => "offer.withdrawn", 5 => "work.awarded", 6 => "note.appended", 7 => "work.result", _ => "",
    };

    private static async Task<Result> AppendOfferInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildEnvelope envelope, GuildCohortProgress notice, StringBuilder prompt, CancellationToken ct)
    {
        var offer = await GuildCohortStore.ReadOfferWithinAsync(connection, transaction, notice.ObjectId, ct).ConfigureAwait(false);

        if (offer.IsFailure) return offer.Error;

        if (offer.Value.CohortId != notice.CohortId || offer.Value.IssuerQuestId != envelope.QuestId
            || offer.Value.OfferSequence != notice.Sequence) return Unavailable;

        var visible = await RequireOfferInputScopeWithinAsync(connection, transaction, caller, offer.Value, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible;

        var goal = await ReadDistributionBodyWithinAsync(connection, transaction, offer.Value.GoalRef, ct).ConfigureAwait(false);

        if (goal.IsFailure) return goal.Error;

        await using var grant = Command(connection, transaction,
            "SELECT CASE WHEN typeof(RequestedGrantJson)='text' AND length(CAST(RequestedGrantJson AS BLOB))<=65536 THEN RequestedGrantJson END FROM guild_work_offers WHERE Id=$offer");

        Add(grant, "$offer", Id(offer.Value.Id));

        if (await grant.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string requested) return Unavailable;

        prompt.AppendLine($"Work offer: {offer.Value.Id:D}");
        prompt.AppendLine($"Publication sequence: {notice.Sequence.ToString(CultureInfo.InvariantCulture)}");
        prompt.AppendLine($"Result type: {offer.Value.ResultContract.MimeType}");

        SystemPromptBuilder.AppendUntrusted(prompt, "Offered skill", offer.Value.SkillId);
        SystemPromptBuilder.AppendUntrusted(prompt, "Requested grant (data, not execution authority)", requested);
        SystemPromptBuilder.AppendUntrusted(prompt, "Offered work", goal.Value);

        return Result.Success();
    }

    private static async Task<Result> RequireOfferInputScopeWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, WorkOfferDto offer, CancellationToken ct)
    {
        if (caller.Kind == GuildCallerKind.Operator || caller.Execution?.QuestId == offer.IssuerQuestId) return Result.Success();

        if (caller.Execution is not { } execution) return Forbidden;

        await using var recipient = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_work_offer_recipients r
                JOIN guild_cohort_members m ON m.Id=r.MemberId AND m.CohortId=$cohort AND m.StateCode=1
                JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.Active=1 AND a.QuestId=$quest
                WHERE r.OfferId=$offer AND r.AgentId=m.AgentId AND r.PrimaryQuestId=m.PrimaryVotingQuestId)
            """);

        Add(recipient, "$cohort", Id(offer.CohortId));
        Add(recipient, "$quest", Id(execution.QuestId));
        Add(recipient, "$offer", Id(offer.Id));

        return Equals(1L, await recipient.ExecuteScalarAsync(ct).ConfigureAwait(false)) ? Result.Success() : Forbidden;
    }

    private static async Task<Result> AppendBidInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildEnvelope envelope, GuildCohortProgress notice, StringBuilder prompt, CancellationToken ct)
    {
        var bid = await GuildCohortStore.ReadBidWithinAsync(connection, transaction, notice.ObjectId, ct).ConfigureAwait(false);

        if (bid.IsFailure) return bid.Error;

        await using var scope = Command(connection, transaction, """
            SELECT o.IssuerQuestId,b.AuthorQuestId FROM guild_work_bids b JOIN guild_work_offers o ON o.Id=b.OfferId
            WHERE b.Id=$bid AND o.CohortId=$cohort
                AND length(CAST(o.IssuerQuestId AS BLOB))=36 AND length(CAST(b.AuthorQuestId AS BLOB))=36
            """);

        Add(scope, "$bid", Id(bid.Value.Id));
        Add(scope, "$cohort", Id(notice.CohortId));

        await using (var reader = await scope.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || !Guid.TryParseExact(reader.GetString(0), "D", out Guid issuer)
                || !Guid.TryParseExact(reader.GetString(1), "D", out Guid author) || author != envelope.QuestId
                || bid.Value.BidSequence != notice.Sequence) return Unavailable;

            if (caller.Kind != GuildCallerKind.Operator && caller.Execution?.QuestId != issuer && caller.Execution?.QuestId != author) return Forbidden;
        }

        var note = await ReadDistributionBodyWithinAsync(connection, transaction, bid.Value.NoteRef, ct).ConfigureAwait(false);

        if (note.IsFailure) return note.Error;

        prompt.AppendLine($"Bid: {bid.Value.Id:D}");
        prompt.AppendLine($"Offer: {bid.Value.OfferId:D}");
        prompt.AppendLine($"Bidder member: {bid.Value.MemberId:D}");
        prompt.AppendLine($"Bidder Agent: {bid.Value.AgentId:D}");
        prompt.AppendLine($"Card revision: {bid.Value.CardRevision.ToString(CultureInfo.InvariantCulture)}");
        prompt.AppendLine($"Claimed cost (unverified): {bid.Value.ClaimedCostUsd?.ToString(CultureInfo.InvariantCulture) ?? "unspecified"}");

        SystemPromptBuilder.AppendUntrusted(prompt, "Bid note", note.Value);

        return Result.Success();
    }

    private static async Task<Result> AppendNoteInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildEnvelope envelope, GuildCohortProgress notice, StringBuilder prompt, CancellationToken ct)
    {
        var note = await GuildCohortStore.ReadNoteWithinAsync(connection, transaction, notice.CohortId, notice.ObjectId, false, ct).ConfigureAwait(false);

        if (note.IsFailure) return note.Error;

        if (note.Value.Sequence != notice.Sequence || note.Value.AuthorQuestId is { } author && author != envelope.QuestId
            || note.Value.OperatorPrincipalId is { } principal && envelope.Producer.OperatorPrincipalId != Id(principal)) return Unavailable;

        if (note.Value.BodyRef is not { } reference) return new Error(ErrorCodes.Guild.ContentErased, "The referenced shared note is unavailable.");

        var body = await ReadDistributionBodyWithinAsync(connection, transaction, reference, ct).ConfigureAwait(false);

        if (body.IsFailure) return body.Error;

        prompt.AppendLine($"Shared note: {note.Value.Id:D}");
        prompt.AppendLine($"Note kind: {note.Value.Kind}");
        prompt.AppendLine($"Note sequence: {note.Value.Sequence.ToString(CultureInfo.InvariantCulture)}");

        if (note.Value.SubjectId is { } subject) prompt.AppendLine($"Subject: {subject:D}");
        if (note.Value.CorrectsNoteId is { } corrected) prompt.AppendLine($"Corrects note: {corrected:D}");

        SystemPromptBuilder.AppendUntrusted(prompt, "Shared note body", body.Value);

        return Result.Success();
    }

    private static async Task<Result> AppendAwardResultInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildEnvelope envelope, GuildCohortProgress notice, StringBuilder prompt, CancellationToken ct)
    {
        if (notice.ObjectId != envelope.QuestId || notice.Sequence != 1 || envelope.Producer.Kind != GuildProducerKind.HostReceipt
            || envelope.Producer.SourceKind != GuildReceiptSourceKind.Result) return Unavailable;

        var proof = await ReadAwardResultPublicationWithinAsync(connection, transaction, notice.ObjectId, ct).ConfigureAwait(false);

        if (proof.IsFailure) return proof.Error;

        if (proof.Value is not { } receipt || receipt.Cohort != notice.CohortId || receipt.Award != envelope.Producer.DomainOwnerId
            || receipt.Result != envelope.Producer.ReceiptId) return Unavailable;

        if (caller.Kind != GuildCallerKind.Operator && caller.Execution?.QuestId != receipt.Primary) return Forbidden;

        var work = await GuildQuestStore.ReadWithinAsync(connection, transaction, notice.ObjectId, ct).ConfigureAwait(false);

        if (work.IsFailure) return work.Error;

        if (work.Value.Quest.RootQuestId != envelope.RootQuestId || work.Value.Quest.ResultReceiptId != receipt.Result) return Unavailable;

        QuestState outcome;
        string? reason;

        await using (var terminal = Command(connection, transaction, """
            SELECT OutcomeCode,CASE WHEN ReasonCode IS NULL OR typeof(ReasonCode)='text' AND length(CAST(ReasonCode AS BLOB))<=128 THEN ReasonCode END
            FROM guild_result_receipts WHERE QuestId=$quest AND ResultId=$result AND OutcomeCode IN(3,4,5,6)
            """))
        {
            Add(terminal, "$quest", Id(notice.ObjectId));
            Add(terminal, "$result", Id(receipt.Result));

            await using var reader = await terminal.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetValue(0) is not long code || code is < 3 or > 6) return Unavailable;

            outcome = (QuestState)code;
            reason = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        prompt.AppendLine($"Award: {receipt.Award:D}");
        prompt.AppendLine($"Awarded Quest: {notice.ObjectId:D}");
        prompt.AppendLine($"Winning primary: {receipt.Primary:D}");
        prompt.AppendLine($"Result receipt: {receipt.Result:D}");
        prompt.AppendLine($"Terminal outcome: {outcome}");

        if (reason is not null) SystemPromptBuilder.AppendUntrusted(prompt, "Terminal reason (data)", reason);

        if (work.Value.Quest.ResultRef is { } result)
        {
            var body = await ReadDistributionBodyWithinAsync(connection, transaction, result, ct).ConfigureAwait(false);

            if (body.IsFailure) return body.Error;

            SystemPromptBuilder.AppendUntrusted(prompt, "Award result body", body.Value);
        }

        return Result.Success();
    }

    private static async Task<Result> AppendAwardInputWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildEnvelope envelope, GuildCohortProgress notice, StringBuilder prompt, CancellationToken ct)
    {
        Guid offerId;
        Guid workId;
        Guid member;

        await using (var scope = Command(connection, transaction, """
            SELECT a.OfferId,a.QuestId,a.MemberId,o.IssuerQuestId,o.OfferSequence FROM guild_work_awards a
            JOIN guild_work_offers o ON o.Id=a.OfferId WHERE a.Id=$award AND o.CohortId=$cohort
                AND length(CAST(a.OfferId AS BLOB))=36 AND length(CAST(a.QuestId AS BLOB))=36
                AND length(CAST(a.MemberId AS BLOB))=36 AND length(CAST(o.IssuerQuestId AS BLOB))=36
            """))
        {
            Add(scope, "$award", Id(notice.ObjectId));
            Add(scope, "$cohort", Id(notice.CohortId));

            await using var reader = await scope.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || !Guid.TryParseExact(reader.GetString(0), "D", out offerId) || !Guid.TryParseExact(reader.GetString(1), "D", out workId)
                || !Guid.TryParseExact(reader.GetString(2), "D", out member) || !Guid.TryParseExact(reader.GetString(3), "D", out Guid issuer)
                || issuer != envelope.QuestId || reader.GetValue(4) is not long sequence || sequence != notice.Sequence) return Unavailable;

            if (caller.Kind != GuildCallerKind.Operator && caller.Execution?.QuestId != issuer)
            {
                await reader.DisposeAsync().ConfigureAwait(false);

                await using var winner = Command(connection, transaction,
                    "SELECT EXISTS(SELECT 1 FROM guild_cohort_member_quests WHERE MemberId=$member AND QuestId=$quest AND Active=1)");

                Add(winner, "$member", Id(member));
                Add(winner, "$quest", Id(caller.Execution?.QuestId));

                if (!Equals(1L, await winner.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Forbidden;
            }
        }

        var offer = await GuildCohortStore.ReadOfferWithinAsync(connection, transaction, offerId, ct).ConfigureAwait(false);

        if (offer.IsFailure) return offer.Error;

        var work = await GuildQuestStore.ReadWithinAsync(connection, transaction, workId, ct).ConfigureAwait(false);

        if (work.IsFailure) return work.Error;

        if (work.Value.Quest.RootQuestId != envelope.RootQuestId || work.Value.Quest.ParentQuestId != envelope.QuestId) return Unavailable;

        var body = await ReadDistributionBodyWithinAsync(connection, transaction, work.Value.Quest.GoalRef, ct).ConfigureAwait(false);

        if (body.IsFailure) return body.Error;

        prompt.AppendLine($"Award: {notice.ObjectId:D}");
        prompt.AppendLine($"Offer: {offerId:D}");
        prompt.AppendLine($"Awarded Quest: {workId:D}");
        prompt.AppendLine($"Winning member: {member:D}");

        SystemPromptBuilder.AppendUntrusted(prompt, "Awarded work", body.Value);

        return Result.Success();
    }
}
