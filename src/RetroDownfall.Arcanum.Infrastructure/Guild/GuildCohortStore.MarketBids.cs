using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal async Task<Result<WorkBidDto>> SubmitBidAsync(GuildExecutionHandle execution, GuildMutationIdentity mutation, Guid offerId,
        SubmitBidRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (execution is null || mutation is null || request is null || offerId == Guid.Empty || request.IdempotencyKey == Guid.Empty
            || mutation.IdempotencyKey != request.IdempotencyKey || request.ExpectedRevision < 1 || request.Note is null || request.ClaimedCostUsd < 0)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The bid request is invalid.");

        byte[] body;

        try { body = new UTF8Encoding(false, true).GetBytes(request.Note); }
        catch (EncoderFallbackException) { return new Error(ErrorCodes.Guild.InvalidRequest, "The bid note is not valid UTF-8."); }

        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.SubmitBidRequest);

        if (body.Length > GuildStore.MaximumContentBytes || encoded.Length > 65_536)
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The bid exceeds its record boundary.");

        byte[] digest = SHA256.HashData(encoded);

        if (mutation.CanonicalRequestDigest != (toolAdmission?.ArgumentsDigest ?? Convert.ToHexString(digest))) return Forbidden;

        var caller = await BidCallerAsync(execution, mutation.IdempotencyKey, toolAdmission, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildQuestDto? acceptedQuest = null;

        var result = await InCohortToolTransactionAsync(execution.RootQuestId, toolAdmission, GuildJsonContext.Default.WorkBidDto,
            async (connection, transaction, signets, token) =>
            {
                var offer = await ReadBidOfferAsync(connection, transaction, offerId, token).ConfigureAwait(false);

                if (offer.IsFailure) return offer.Error;

                var cohortResult = await ReadWithinAsync(connection, transaction, offer.Value.Cohort, token).ConfigureAwait(false);

                if (cohortResult.IsFailure) return cohortResult.Error;

                CohortDto cohort = cohortResult.Value.Cohort;

                if (cohort.RootQuestId != execution.RootQuestId) return Forbidden;

                var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller.Value, cohort, true, token).ConfigureAwait(false);

                if (actor.IsFailure) return actor.Error;

                if (actor.Value.Member is not { } member) return Forbidden;

                if (toolAdmission is not null)
                {
                    if (!GuildMarketToolArguments.MatchesSubmit(toolAdmission, offerId, request)) return Forbidden;

                    var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (admitted.IsFailure) return admitted.Error;

                    acceptedQuest = admitted.Value.Quest;
                }

                var prior = await FindBidMutationAsync(connection, transaction, execution.QuestId, 3, offerId, request.IdempotencyKey, digest, token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } replay) return await BidProjectionAsync(connection, transaction, replay, request.IdempotencyKey, true, token).ConfigureAwait(false);

                if (cohort.State != GuildCohortState.Open || offer.Value.State != GuildWorkOfferState.Open
                    || offer.Value.Revision != request.ExpectedRevision) return Conflict;

                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false)) return Forbidden;

                await using (var occupied = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_work_bids WHERE OfferId=$offer AND MemberId=$member)"))
                {
                    Add(occupied, "$offer", Id(offerId));
                    Add(occupied, "$member", Id(member));

                    if (Equals(1L, await occupied.ExecuteScalarAsync(token).ConfigureAwait(false)))
                        return new Error(ErrorCodes.Guild.BidConflict, "This member already has a bid for the offer.");
                }

                Guid primary;
                Guid agent;
                Guid card;
                long cardRevision;

                await using (var recipient = Command(connection, transaction, """
                    SELECT r.PrimaryQuestId,r.AgentId,r.CardId,r.CardRevision FROM guild_work_offer_recipients r
                    JOIN guild_cohort_members m ON m.Id=r.MemberId AND m.CohortId=$cohort AND m.StateCode=1
                    JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=$quest AND a.Active=1
                    WHERE r.OfferId=$offer AND r.MemberId=$member AND m.AgentId=r.AgentId AND m.PrimaryVotingQuestId=r.PrimaryQuestId
                    """))
                {
                    Add(recipient, "$cohort", Id(cohort.Id));
                    Add(recipient, "$quest", Id(execution.QuestId));
                    Add(recipient, "$offer", Id(offerId));
                    Add(recipient, "$member", Id(member));

                    await using var reader = await recipient.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return Forbidden;

                    if (!Guid.TryParse(reader.GetString(0), out primary) || !Guid.TryParse(reader.GetString(1), out agent)
                        || !Guid.TryParse(reader.GetString(2), out card) || reader.GetValue(3) is not long revision || revision < 1) return Unavailable;

                    cardRevision = revision;
                }

                if (agent != execution.AgentId) return Forbidden;

                var primaryQuest = await GuildQuestStore.ReadWithinAsync(connection, transaction, primary, token).ConfigureAwait(false);

                if (primaryQuest.IsFailure) return primaryQuest.Error;

                if (primaryQuest.Value.Retired || primaryQuest.Value.Quest.CancellationRequested
                    || primaryQuest.Value.Quest.State is not (QuestState.Submitted or QuestState.Working or QuestState.InputRequired)) return Forbidden;

                var primaryGrant = await signets.ValidateCurrentGrantAsync(primary, token).ConfigureAwait(false);

                if (primaryGrant.IsFailure) return primaryGrant.Error;

                await using (var currentCard = Command(connection, transaction, "SELECT EXISTS(SELECT 1 FROM guild_agent_cards WHERE CardId=$card AND AgentId=$agent AND CurrentRevision=$revision AND Available=1)"))
                {
                    Add(currentCard, "$card", Id(card));
                    Add(currentCard, "$agent", Id(agent));
                    Add(currentCard, "$revision", cardRevision);

                    if (!Equals(1L, await currentCard.ExecuteScalarAsync(token).ConfigureAwait(false)))
                        return new Error(ErrorCodes.Guild.DestinationChanged, "The published recipient Card has changed.");
                }

                if (offer.Value.Sequence == long.MaxValue) return Unavailable;

                long sequence = offer.Value.Sequence + 1;
                Guid id = Guid.NewGuid();
                Guid content = Guid.NewGuid();
                Guid receiptId = Guid.NewGuid();
                DateTimeOffset now = time.GetUtcNow();

                var label = await GuildQuestStore.ResultSensitivityWithinAsync(connection, transaction, actor.Value.Source, content, body, null, token).ConfigureAwait(false);

                if (label.IsFailure) return label.Error;

                var written = await GuildStore.WriteContentWithinAsync(connection, transaction,
                    new(content, id, GuildContentKind.Bid, 1, actor.Value.Source.Quest.SessionId,
                        actor.Value.Source.Quest.CampaignId, "text/plain", body, label.Value), now, token).ConfigureAwait(false);

                if (written.IsFailure) return written.Error;

                await using (var advance = Command(connection, transaction, "UPDATE guild_work_offers SET BidSequence=$next WHERE Id=$offer AND StateCode=1 AND Revision=$revision AND BidSequence=$previous"))
                {
                    Add(advance, "$next", sequence);
                    Add(advance, "$offer", Id(offerId));
                    Add(advance, "$revision", request.ExpectedRevision);
                    Add(advance, "$previous", offer.Value.Sequence);

                    if (await advance.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                await using (var insert = Command(connection, transaction, """
                    INSERT INTO guild_work_bids(Id,OfferId,MemberId,AgentId,AuthorQuestId,CardRevision,BidSequence,Revision,StateCode,NoteContentId,ClaimedCostUsd)
                    VALUES($id,$offer,$member,$agent,$quest,$cardRevision,$sequence,1,1,$content,$cost)
                    """))
                {
                    Add(insert, "$id", Id(id));
                    Add(insert, "$offer", Id(offerId));
                    Add(insert, "$member", Id(member));
                    Add(insert, "$agent", Id(agent));
                    Add(insert, "$quest", Id(execution.QuestId));
                    Add(insert, "$cardRevision", cardRevision);
                    Add(insert, "$sequence", sequence);
                    Add(insert, "$content", Id(content));
                    Add(insert, "$cost", request.ClaimedCostUsd is { } cost ? ExactUsdText.Format(cost) : null);

                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var receipt = new BidMutation(id, receiptId, 1, now);

                await InsertBidMutationAsync(connection, transaction, execution.QuestId, 3, offerId, request.IdempotencyKey, digest, receipt, token).ConfigureAwait(false);

                var change = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, execution.QuestId,
                    GuildEventKind.BidChanged, id, 1, null, now, token).ConfigureAwait(false);

                if (change.IsFailure) return change.Error;

                var notification = await new GuildPortalStore(quests, authority, policy, time).PublishDistributionWithinAsync(connection, transaction, signets,
                    caller.Value, actor.Value.Source, cohort, new("bid.submitted", cohort.Id, id, sequence), Guid.NewGuid(), null, now, token, offer.Value.Issuer).ConfigureAwait(false);

                if (notification.IsFailure) return notification.Error;

                return await BidProjectionAsync(connection, transaction, receipt, request.IdempotencyKey, false, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedQuest is not null)
            toolAdmission.RecordCommittedProjection(acceptedQuest, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.WorkBidDto));

        return result;
    }

    internal async Task<Result<WorkBidDto>> WithdrawBidAsync(GuildExecutionHandle execution, GuildMutationIdentity mutation, Guid bidId,
        GuildRevisionRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (execution is null || mutation is null || request is null || bidId == Guid.Empty || request.IdempotencyKey == Guid.Empty
            || mutation.IdempotencyKey != request.IdempotencyKey || request.ExpectedRevision < 1)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The bid withdrawal request is invalid.");

        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.GuildRevisionRequest));

        if (mutation.CanonicalRequestDigest != (toolAdmission?.ArgumentsDigest ?? Convert.ToHexString(digest))) return Forbidden;

        var caller = await BidCallerAsync(execution, mutation.IdempotencyKey, toolAdmission, ct).ConfigureAwait(false);

        if (caller.IsFailure) return caller.Error;

        GuildQuestDto? acceptedQuest = null;

        var result = await InCohortToolTransactionAsync(execution.RootQuestId, toolAdmission, GuildJsonContext.Default.WorkBidDto,
            async (connection, transaction, signets, token) =>
            {
                Guid offerId;
                Guid bidder;
                long revision;
                long sequence;
                long state;

                await using (var bid = Command(connection, transaction, "SELECT OfferId,MemberId,Revision,BidSequence,StateCode FROM guild_work_bids WHERE Id=$bid"))
                {
                    Add(bid, "$bid", Id(bidId));

                    await using var reader = await bid.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (!await reader.ReadAsync(token).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.NotFound, "The bid is unavailable.");

                    if (!Guid.TryParse(reader.GetString(0), out offerId) || !Guid.TryParse(reader.GetString(1), out bidder)
                        || reader.GetValue(2) is not long r || reader.GetValue(3) is not long s || reader.GetValue(4) is not long status) return Unavailable;

                    revision = r;
                    sequence = s;
                    state = status;
                }

                var offer = await ReadBidOfferAsync(connection, transaction, offerId, token).ConfigureAwait(false);

                if (offer.IsFailure) return offer.Error;

                var cohortResult = await ReadWithinAsync(connection, transaction, offer.Value.Cohort, token).ConfigureAwait(false);

                if (cohortResult.IsFailure) return cohortResult.Error;

                CohortDto cohort = cohortResult.Value.Cohort;

                if (cohort.RootQuestId != execution.RootQuestId) return Forbidden;

                var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller.Value, cohort, true, token).ConfigureAwait(false);

                if (actor.IsFailure) return actor.Error;

                if (actor.Value.Member != bidder) return Forbidden;

                if (toolAdmission is not null)
                {
                    if (!GuildMarketToolArguments.MatchesWithdrawBid(toolAdmission, bidId, request)) return Forbidden;

                    var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (admitted.IsFailure) return admitted.Error;

                    acceptedQuest = admitted.Value.Quest;
                }

                var prior = await FindBidMutationAsync(connection, transaction, execution.QuestId, 4, bidId, request.IdempotencyKey, digest, token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } replay) return await BidProjectionAsync(connection, transaction, replay, request.IdempotencyKey, true, token).ConfigureAwait(false);

                if (cohort.State != GuildCohortState.Open || offer.Value.State != GuildWorkOfferState.Open || state != 1
                    || revision != request.ExpectedRevision || revision == long.MaxValue) return Conflict;

                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, execution.QuestId, token).ConfigureAwait(false)) return Forbidden;

                await using (var update = Command(connection, transaction, "UPDATE guild_work_bids SET StateCode=2,Revision=Revision+1 WHERE Id=$bid AND StateCode=1 AND Revision=$revision"))
                {
                    Add(update, "$bid", Id(bidId));
                    Add(update, "$revision", revision);

                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                DateTimeOffset now = time.GetUtcNow();
                var receipt = new BidMutation(bidId, Guid.NewGuid(), revision + 1, now);

                await InsertBidMutationAsync(connection, transaction, execution.QuestId, 4, bidId, request.IdempotencyKey, digest, receipt, token).ConfigureAwait(false);

                var change = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, execution.QuestId,
                    GuildEventKind.BidChanged, bidId, receipt.Revision, null, now, token).ConfigureAwait(false);

                if (change.IsFailure) return change.Error;

                var notification = await new GuildPortalStore(quests, authority, policy, time).PublishDistributionWithinAsync(connection, transaction, signets,
                    caller.Value, actor.Value.Source, cohort, new("bid.withdrawn", cohort.Id, bidId, sequence), Guid.NewGuid(), null, now, token, offer.Value.Issuer).ConfigureAwait(false);

                if (notification.IsFailure) return notification.Error;

                return await BidProjectionAsync(connection, transaction, receipt, request.IdempotencyKey, false, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedQuest is not null)
            toolAdmission.RecordCommittedProjection(acceptedQuest, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.WorkBidDto));

        return result;
    }

    private async Task<Result<GuildCallerContext>> BidCallerAsync(GuildExecutionHandle execution, Guid invocation,
        GuildChildToolAdmission? admission, CancellationToken ct)
    {
        if (admission is not null)
        {
            GuildCallerContext? caller = admission.Registry.Parent.Driver.Caller;

            return caller is not null && ReferenceEquals(execution, admission.Registry.Parent.Driver.Execution)
                && ReferenceEquals(caller.Execution, execution) ? caller : Forbidden;
        }

        return policy is GuildLocalHostPolicy host
            ? await host.CreateRunCallerAsync(authority, execution, invocation, ct).ConfigureAwait(false) : Forbidden;
    }

    private sealed record BidOffer(Guid Cohort, Guid Issuer, GuildWorkOfferState State, long Revision, long Sequence);

    private sealed record BidMutation(Guid Id, Guid Receipt, long Revision, DateTimeOffset Accepted);

    private static async Task<Result<BidOffer>> ReadBidOfferAsync(SqliteConnection connection, SqliteTransaction transaction, Guid offer, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, "SELECT CohortId,IssuerQuestId,StateCode,Revision,BidSequence FROM guild_work_offers WHERE Id=$offer");

        Add(command, "$offer", Id(offer));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.NotFound, "The offer is unavailable.");

        if (!Guid.TryParse(reader.GetString(0), out Guid cohort) || !Guid.TryParse(reader.GetString(1), out Guid issuer)
            || reader.GetValue(2) is not long state || state is < 1 or > 4 || reader.GetValue(3) is not long revision || revision < 1
            || reader.GetValue(4) is not long sequence || sequence < 0) return Unavailable;

        return new BidOffer(cohort, issuer, (GuildWorkOfferState)state, revision, sequence);
    }

    private static async Task<Result<BidMutation?>> FindBidMutationAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid quest, int action, Guid parent, Guid key, byte[] digest, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_work_mutations
            WHERE PrincipalKindCode=2 AND PrincipalId=$quest AND ActionCode=$action AND ParentId=$parent AND IdempotencyKey=$key
            """);

        Add(command, "$quest", Id(quest));
        Add(command, "$action", action);
        Add(command, "$parent", Id(parent));
        Add(command, "$key", Id(key));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<BidMutation?>.Success(null);

        if (reader.GetValue(4) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The bid mutation identity already has different input.");

        if (!Guid.TryParse(reader.GetString(0), out Guid id) || !Guid.TryParse(reader.GetString(1), out Guid receipt)
            || reader.GetValue(2) is not long revision || revision < 1 || !UtcInstantText.TryParse(reader.GetString(3), out DateTimeOffset accepted)
            || UtcInstantText.Format(accepted) != reader.GetString(3)) return Unavailable;

        return new BidMutation(id, receipt, revision, accepted);
    }

    private static async Task InsertBidMutationAsync(SqliteConnection connection, SqliteTransaction transaction, Guid quest, int action,
        Guid parent, Guid key, byte[] digest, BidMutation receipt, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
            VALUES(2,$quest,$action,$parent,$key,$digest,$result,$receipt,$revision,$accepted)
            """);

        Add(command, "$quest", Id(quest));
        Add(command, "$action", action);
        Add(command, "$parent", Id(parent));
        Add(command, "$key", Id(key));
        Add(command, "$digest", digest);
        Add(command, "$result", Id(receipt.Id));
        Add(command, "$receipt", Id(receipt.Receipt));
        Add(command, "$revision", receipt.Revision);
        Add(command, "$accepted", UtcInstantText.Format(receipt.Accepted));

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<Result<WorkBidDto>> BidProjectionAsync(SqliteConnection connection, SqliteTransaction transaction,
        BidMutation mutation, Guid key, bool replay, CancellationToken ct)
    {
        var bid = await ReadBidWithinAsync(connection, transaction, mutation.Id, ct).ConfigureAwait(false);

        return bid.IsFailure ? bid.Error : bid.Value with { Receipt = new(mutation.Receipt, key, replay, mutation.Accepted, mutation.Revision) };
    }
}
