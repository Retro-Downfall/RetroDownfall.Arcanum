using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal async Task<Result<WorkOfferDto>> PostOfferAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid cohortId,
        PostWorkOfferRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller is null || mutation is null || request is null || cohortId == Guid.Empty
            || request.IdempotencyKey == Guid.Empty || mutation.IdempotencyKey != request.IdempotencyKey
            || request.ExpectedCohortRevision < 1 || string.IsNullOrWhiteSpace(request.SkillId)
            || string.IsNullOrWhiteSpace(request.Goal) || request.ResultContract is null
            || request.ResultContract.MimeType is not ("text/plain" or "application/json"))
            return new Error(ErrorCodes.Guild.InvalidRequest, "The work offer request is invalid.");

        byte[] body;

        try { body = new UTF8Encoding(false, true).GetBytes(request.Goal); }
        catch (EncoderFallbackException) { return new Error(ErrorCodes.Guild.InvalidRequest, "The offer body is not valid UTF-8."); }

        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.PostWorkOfferRequest);

        if (body.Length > GuildStore.MaximumContentBytes || encoded.Length > 65_536)
            return new Error(ErrorCodes.Guild.PayloadTooLarge, "The work offer exceeds its record boundary.");

        byte[] digest = SHA256.HashData(encoded);

        if (mutation.CanonicalRequestDigest != (toolAdmission?.ArgumentsDigest ?? Convert.ToHexString(digest))) return Forbidden;

        Result<Guid> root = await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        GuildQuestDto? acceptedQuest = null;

        var result = await InCohortToolTransactionAsync(root.Value, toolAdmission, GuildJsonContext.Default.WorkOfferDto,
            async (connection, transaction, signets, token) =>
            {
                var found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                CohortDto cohort = found.Value.Cohort;

                if (cohort.RootQuestId != root.Value) return Forbidden;

                var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, cohort, true, token).ConfigureAwait(false);

                if (actor.IsFailure) return actor.Error;

                if (toolAdmission is not null)
                {
                    if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                        || !GuildMarketToolArguments.MatchesPost(toolAdmission, cohortId, request)) return Forbidden;

                    var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                    if (admitted.IsFailure) return admitted.Error;

                    acceptedQuest = admitted.Value.Quest;
                }

                int principalKind = caller.Kind == GuildCallerKind.Operator ? 1 : 2;
                Guid principalId = principalKind == 1 ? caller.PrincipalId!.Value : actor.Value.Source.Quest.Id;

                await using (var prior = Command(connection, transaction, """
                    SELECT ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_work_mutations
                    WHERE PrincipalKindCode=$kind AND PrincipalId=$principal AND ActionCode=1 AND ParentId=$cohort AND IdempotencyKey=$key
                    """))
                {
                    Add(prior, "$kind", principalKind);
                    Add(prior, "$principal", Id(principalId));
                    Add(prior, "$cohort", Id(cohortId));
                    Add(prior, "$key", Id(request.IdempotencyKey));

                    await using var reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        if (reader.GetValue(4) is not byte[] existing || !CryptographicOperations.FixedTimeEquals(existing, digest))
                            return new Error(ErrorCodes.Guild.IdempotencyConflict, "The offer identity was accepted with different input.");

                        Guid saved = Guid.Parse(reader.GetString(0));
                        Guid receipt = Guid.Parse(reader.GetString(1));
                        long acceptedRevision = reader.GetInt64(2);
                        DateTimeOffset acceptedAt = GuildMarketStorage.ParseReceiptInstant(reader.GetString(3));

                        await reader.DisposeAsync().ConfigureAwait(false);

                        var replay = await ReadOfferWithinAsync(connection, transaction, saved, token).ConfigureAwait(false);

                        return replay.IsFailure ? replay.Error : replay.Value with
                        {
                            Receipt = new(receipt, request.IdempotencyKey, true, acceptedAt, acceptedRevision),
                        };
                    }
                }

                if (cohort.State != GuildCohortState.Open || cohort.Revision != request.ExpectedCohortRevision)
                    return RevisionConflict(cohort);

                if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, actor.Value.Source.Quest.Id, token).ConfigureAwait(false)) return Forbidden;

                var issuer = await GuildSignetStore.ReadAsync(connection, transaction, actor.Value.Source.Quest.Id, true, token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                var issuerLive = await signets.ValidateCurrentGrantAsync(actor.Value.Source.Quest.Id, token).ConfigureAwait(false);

                if (issuerLive.IsFailure) return issuerLive.Error;

                if (policy is not GuildLocalHostPolicy local) return Unavailable;

                if (toolAdmission is not null)
                {
                    Result checkedGrant = await local.ValidateTaskGrantWithinAsync(connection, transaction, issuerLive.Value,
                        request.RequestedGrant, token).ConfigureAwait(false);
                    if (checkedGrant.IsFailure) return checkedGrant.Error;
                }
                else
                {
                    var requested = await local.ResolveCohortGrantWithinAsync(connection, transaction, issuerLive.Value,
                        request.RequestedGrant, token).ConfigureAwait(false);
                    if (requested.IsFailure) return requested.Error;
                }

                var lineage = await GuildSignetStore.LineageAsync(connection, transaction, issuer.Value, token).ConfigureAwait(false);

                if (lineage.IsFailure) return lineage.Error;

                long sequence;

                await using (var counter = Command(connection, transaction, "SELECT OfferSequence FROM guild_cohorts WHERE Id=$cohort"))
                {
                    Add(counter, "$cohort", Id(cohortId));

                    if (await counter.ExecuteScalarAsync(token).ConfigureAwait(false) is not long current || current == long.MaxValue || cohort.Revision == long.MaxValue) return Unavailable;

                    sequence = current + 1;
                }

                Guid offer = Guid.NewGuid();
                Guid contentId = Guid.NewGuid();
                Guid receiptId = Guid.NewGuid();
                DateTimeOffset now = time.GetUtcNow();

                var label = await GuildQuestStore.ResultSensitivityWithinAsync(connection, transaction, actor.Value.Source, contentId, body, null, token).ConfigureAwait(false);

                if (label.IsFailure) return label.Error;

                var content = await GuildStore.WriteContentWithinAsync(connection, transaction,
                    new(contentId, offer, GuildContentKind.Offer, 1, actor.Value.Source.Quest.SessionId,
                        actor.Value.Source.Quest.CampaignId, "text/plain", body, label.Value), now, token).ConfigureAwait(false);

                if (content.IsFailure) return content.Error;

                string grantJson = JsonSerializer.Serialize(request.RequestedGrant, GuildJsonContext.Default.GuildRequestedGrant);

                await using (var insert = Command(connection, transaction, """
                    INSERT INTO guild_work_offers(Id,CohortId,IssuerQuestId,OfferSequence,SkillId,GoalContentId,RequestedGrantJson,BudgetHint,MimeType,StateCode,Revision)
                    VALUES($id,$cohort,$issuer,$sequence,$skill,$content,$grant,$hint,$mime,1,1)
                    """))
                {
                    Add(insert, "$id", Id(offer));
                    Add(insert, "$cohort", Id(cohortId));
                    Add(insert, "$issuer", Id(actor.Value.Source.Quest.Id));
                    Add(insert, "$sequence", sequence);
                    Add(insert, "$skill", request.SkillId);
                    Add(insert, "$content", Id(contentId));
                    Add(insert, "$grant", grantJson);
                    Add(insert, "$hint", toolAdmission is null ? 0 : 1);
                    Add(insert, "$mime", request.ResultContract.MimeType);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                string after = "";
                long eligibleCount = 0;

                while (true)
                {
                    Guid memberId;
                    Guid agentId;
                    Guid primaryId;
                    Guid cardId;
                    long cardRevision;
                    int sourceKind;

                    await using (var next = Command(connection, transaction, """
                        SELECT m.Id,m.AgentId,m.PrimaryVotingQuestId,c.CardId,c.CurrentRevision,c.SourceKindCode
                        FROM guild_cohort_members m
                        JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=m.PrimaryVotingQuestId AND a.AssociationKindCode=1 AND a.Active=1
                        JOIN guild_quests q ON q.Id=m.PrimaryVotingQuestId AND q.AgentId=m.AgentId AND q.Retired=0 AND q.CancellationRequested=0
                        JOIN LongRunningOperations o ON o.Id=q.OperationId AND o.State IN(0,1,2)
                        JOIN guild_agent_cards c ON c.AgentId=m.AgentId AND c.Available=1
                        WHERE m.CohortId=$cohort AND m.StateCode=1 AND m.Id>$after AND c.SourceKindCode<>2
                            AND (c.WorkspaceId IS NULL OR c.WorkspaceId=$workspace)
                            AND EXISTS(SELECT 1 FROM guild_agent_card_skills k WHERE k.CardId=c.CardId AND k.Revision=c.CurrentRevision AND k.SkillId=$skill)
                        ORDER BY m.Id LIMIT 1
                        """))
                    {
                        Add(next, "$cohort", Id(cohortId));
                        Add(next, "$after", after);
                        Add(next, "$workspace", actor.Value.Source.Quest.WorkspaceId is { } workspace ? Id(workspace) : null);
                        Add(next, "$skill", request.SkillId);

                        await using var reader = await next.ExecuteReaderAsync(token).ConfigureAwait(false);

                        if (!await reader.ReadAsync(token).ConfigureAwait(false)) break;

                        if (!Guid.TryParse(reader.GetString(0), out memberId) || !Guid.TryParse(reader.GetString(1), out agentId)
                            || !Guid.TryParse(reader.GetString(2), out primaryId) || !Guid.TryParse(reader.GetString(3), out cardId)
                            || reader.GetValue(4) is not long revision || revision < 1 || reader.GetValue(5) is not long kind) return Unavailable;

                        cardRevision = revision;
                        sourceKind = checked((int)kind);
                    }

                    after = Id(memberId);

                    if (lineage.Value.Any(item => item.Binding.AgentId == agentId)) continue;

                    GuildDelegationKind edge = sourceKind == (int)GuildAgentSourceKind.Remote ? GuildDelegationKind.Remote : GuildDelegationKind.Local;

                    if (!issuerLive.Value.Delegation.Contains(edge)) continue;

                    var live = await signets.ValidateCurrentGrantAsync(primaryId, token).ConfigureAwait(false);

                    if (live.IsFailure)
                    {
                        if (live.Error.Code is ErrorCodes.Guild.AuthorityRevoked or ErrorCodes.Guild.AuthorityExpired or ErrorCodes.Guild.Forbidden) continue;
                        return live.Error;
                    }

                    var visibleCard = await GuildStore.ResolveQuestCardWithinAsync(connection, transaction, agentId, cardId, cardRevision,
                        new(actor.Value.Source.Quest.WorkspaceId, actor.Value.Source.Quest.SessionId, actor.Value.Source.Quest.CampaignId), token).ConfigureAwait(false);

                    if (visibleCard.IsFailure)
                    {
                        if (visibleCard.Error.Code is ErrorCodes.Guild.NotFound or ErrorCodes.Guild.Forbidden) continue;
                        return visibleCard.Error;
                    }

                    await using (var recipient = Command(connection, transaction, """
                        INSERT INTO guild_work_offer_recipients(OfferId,MemberId,AgentId,PrimaryQuestId,CardId,CardRevision)
                        VALUES($offer,$member,$agent,$primary,$card,$revision)
                        """))
                    {
                        Add(recipient, "$offer", Id(offer));
                        Add(recipient, "$member", Id(memberId));
                        Add(recipient, "$agent", Id(agentId));
                        Add(recipient, "$primary", Id(primaryId));
                        Add(recipient, "$card", Id(cardId));
                        Add(recipient, "$revision", cardRevision);
                        await recipient.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }

                    var notice = await new GuildPortalStore(quests, authority, policy, time).PublishDistributionWithinAsync(
                        connection, transaction, signets, caller, actor.Value.Source, cohort,
                        new("offer.posted", cohortId, offer, sequence), Guid.NewGuid(), memberId, now, token, primaryId).ConfigureAwait(false);

                    if (notice.IsFailure) return notice.Error;

                    eligibleCount++;
                }

                if (eligibleCount == 0) return new Error(ErrorCodes.Guild.NoEligibleBidder, "No current Cohort member is eligible for this work.");

                var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId,
                    actor.Value.Source.Quest.Id, GuildEventKind.OfferChanged, offer, 1, null, now, token).ConfigureAwait(false);

                if (projected.IsFailure) return projected.Error;

                await using (var update = Command(connection, transaction, """
                    UPDATE guild_cohorts SET OfferSequence=$sequence,Revision=Revision+1,EventWatermark=$event
                    WHERE Id=$cohort AND StateCode=1 AND Revision=$revision AND OfferSequence=$prior
                    """))
                {
                    Add(update, "$sequence", sequence);
                    Add(update, "$event", projected.Value);
                    Add(update, "$cohort", Id(cohortId));
                    Add(update, "$revision", cohort.Revision);
                    Add(update, "$prior", sequence - 1);
                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;
                }

                await using (var accepted = Command(connection, transaction, """
                    INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
                    VALUES($kind,$principal,1,$parent,$key,$digest,$result,$receipt,1,$now)
                    """))
                {
                    Add(accepted, "$kind", principalKind);
                    Add(accepted, "$principal", Id(principalId));
                    Add(accepted, "$parent", Id(cohortId));
                    Add(accepted, "$key", Id(request.IdempotencyKey));
                    Add(accepted, "$digest", digest);
                    Add(accepted, "$result", Id(offer));
                    Add(accepted, "$receipt", Id(receiptId));
                    Add(accepted, "$now", RetroDownfall.Arcanum.Infrastructure.Data.UtcInstantText.Format(now));
                    await accepted.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                return new WorkOfferDto(offer, cohortId, actor.Value.Source.Quest.Id, sequence, 1, request.SkillId,
                    request.ResultContract, content.Value, GuildWorkOfferState.Open, null, null,
                    new(receiptId, request.IdempotencyKey, false, now, 1));
            }, ct).ConfigureAwait(false);

        if (result.IsSuccess && toolAdmission is not null && acceptedQuest is not null)
            toolAdmission.RecordCommittedProjection(acceptedQuest, JsonSerializer.SerializeToElement(result.Value, GuildJsonContext.Default.WorkOfferDto));

        return result;
    }
}
