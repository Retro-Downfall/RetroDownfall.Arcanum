using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private sealed record PreparedAward(GuildAwardIssue? Context, GuildIssueChildRequest? Child, WorkAwardDto? Replay, GuildQuestDto? ReplayQuest);

    internal async Task<Result<WorkAwardDto>> AwardAsync(GuildCallerContext caller, GuildMutationIdentity mutation, Guid offerId,
        AwardWorkRequest request, CancellationToken ct, GuildChildToolAdmission? toolAdmission = null)
    {
        if (caller is null || mutation is null || request is null || offerId == Guid.Empty || request.BidId == Guid.Empty
            || request.IdempotencyKey == Guid.Empty || mutation.IdempotencyKey != request.IdempotencyKey
            || request.ExpectedRevision < 1 || request.ExpectedCohortRevision < 1)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The award request is invalid.");

        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.AwardWorkRequest));

        if (mutation.CanonicalRequestDigest != (toolAdmission?.ArgumentsDigest ?? Convert.ToHexString(digest))) return Forbidden;

        var root = await OfferRootAsync(caller, offerId, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        var preparation = await authority.InQuestTransactionAsync<PreparedAward>(root.Value, async (connection, transaction, signets, token) =>
        {
            var scope = await OfferScopeWithinAsync(connection, transaction, offerId, token).ConfigureAwait(false);

            if (scope.IsFailure || !await MarketRootMatchesWithinAsync(connection, transaction, scope.Value.Cohort, root.Value, token).ConfigureAwait(false)) return MarketNotFound;

            var found = await ReadWithinAsync(connection, transaction, scope.Value.Cohort, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            CohortDto cohort = found.Value.Cohort;

            if (cohort.RootQuestId != root.Value) return MarketNotFound;

            var actor = await AuthorizeNoteWithinAsync(connection, transaction, signets, caller, cohort, true, token).ConfigureAwait(false);

            if (actor.IsFailure) return actor.Error;

            GuildQuestStored issuerSource = actor.Value.Source;

            if (caller.Kind == GuildCallerKind.Operator)
            {
                var issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, scope.Value.Issuer, token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                var current = await signets.ValidateCurrentGrantAsync(issuer.Value.Quest.Id, token).ConfigureAwait(false);

                if (current.IsFailure) return current.Error;

                issuerSource = issuer.Value;
            }

            if (issuerSource.Quest.Id != scope.Value.Issuer) return MarketNotFound;

            var offer = await ReadOfferWithinAsync(connection, transaction, offerId, token).ConfigureAwait(false);
            if (offer.IsFailure) return offer.Error;

            if (toolAdmission is not null)
            {
                if (!ReferenceEquals(toolAdmission.Registry.Parent.Driver.Caller, caller)
                    || !GuildMarketToolArguments.MatchesAward(toolAdmission, offerId, request)) return Forbidden;

                var admitted = await quests.AuthorizeCohortToolWithinAsync(connection, transaction, signets, toolAdmission, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;
            }

            int principalKind = caller.Kind == GuildCallerKind.Operator ? 1 : 2;
            Guid principalId = principalKind == 1 ? caller.PrincipalId!.Value : actor.Value.Source.Quest.Id;

            await using (var prior = Command(connection, transaction, """
                SELECT ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_work_mutations
                WHERE PrincipalKindCode=$kind AND PrincipalId=$principal AND ActionCode=5 AND ParentId=$offer AND IdempotencyKey=$key
                """))
            {
                Add(prior, "$kind", principalKind);
                Add(prior, "$principal", Id(principalId));
                Add(prior, "$offer", Id(offerId));
                Add(prior, "$key", Id(request.IdempotencyKey));

                await using var reader = await prior.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (reader.GetValue(4) is not byte[] old || !CryptographicOperations.FixedTimeEquals(old, digest))
                        return new Error(ErrorCodes.Guild.IdempotencyConflict, "The award identity was accepted with different input.");

                    Guid id = Guid.Parse(reader.GetString(0));
                    Guid receipt = Guid.Parse(reader.GetString(1));
                    long revision = reader.GetInt64(2);
                    DateTimeOffset accepted = GuildMarketStorage.ParseReceiptInstant(reader.GetString(3));

                    await reader.DisposeAsync().ConfigureAwait(false);

                    var replay = await ReadAwardWithinAsync(connection, transaction, id, token).ConfigureAwait(false);

                    if (replay.IsFailure) return replay.Error;

                    var work = await GuildQuestStore.ReadWithinAsync(connection, transaction, replay.Value.QuestId, token).ConfigureAwait(false);

                    if (work.IsFailure) return work.Error;

                    return new PreparedAward(null, null, replay.Value with
                    {
                        Receipt = new(receipt, request.IdempotencyKey, true, accepted, revision),
                    }, work.Value.Quest);
                }
            }

            if (cohort.State != GuildCohortState.Open || cohort.Revision != request.ExpectedCohortRevision
                || offer.Value.State != GuildWorkOfferState.Open || offer.Value.Revision != request.ExpectedRevision) return Conflict;

            if (await GuildQuestStore.HasClosedAncestorAsync(connection, transaction, issuerSource.Quest.Id, token).ConfigureAwait(false)) return Forbidden;

            var selected = await ReadAwardCandidateWithinAsync(connection, transaction, offer.Value, request.BidId, token).ConfigureAwait(false);

            if (selected.IsFailure) return selected.Error;

            var primary = await GuildQuestStore.ReadWithinAsync(connection, transaction, selected.Value.PrimaryQuest, token).ConfigureAwait(false);

            if (primary.IsFailure) return primary.Error;

            if (primary.Value.Retired || primary.Value.Quest.CancellationRequested
                || primary.Value.Quest.State is not (QuestState.Submitted or QuestState.Working or QuestState.InputRequired)) return Forbidden;

            var primarySignet = await GuildSignetStore.ReadAsync(connection, transaction, selected.Value.PrimaryQuest, true, token).ConfigureAwait(false);

            if (primarySignet.IsFailure) return primarySignet.Error;

            var primaryLive = await signets.ValidateCurrentGrantAsync(selected.Value.PrimaryQuest, token).ConfigureAwait(false);

            if (primaryLive.IsFailure) return primaryLive.Error;

            var goal = await GuildQuestStore.ReadProviderContentWithinAsync(connection, transaction, offer.Value.GoalRef, token).ConfigureAwait(false);

            if (goal.IsFailure) return goal.Error;

            GuildRequestedGrant? requested;

            try { requested = JsonSerializer.Deserialize(selected.Value.GrantJson, GuildJsonContext.Default.GuildRequestedGrant); }
            catch (JsonException) { return Unavailable; }

            GuildAwardIssue context = new(offerId, request.BidId, cohort.Id, cohort.RootQuestId, offer.Value.IssuerQuestId,
                request.ExpectedRevision, request.ExpectedCohortRevision, selected.Value.Member, selected.Value.PrimaryQuest,
                primarySignet.Value.Binding.SignetId, primarySignet.Value.RevocationGeneration, selected.Value.Agent,
                selected.Value.Card, selected.Value.CardRevision, mutation, request, selected.Value.BudgetHint, primaryLive.Value);

            GuildRemoteInvocationOptions? remote = selected.Value.SourceKind == GuildAgentSourceKind.Remote
                ? new(GuildRemoteDispatchMode.Blocking, [offer.Value.ResultContract.MimeType], offer.Value.SkillId) : null;

            GuildIssueChildRequest child = new(request.IdempotencyKey, issuerSource.Quest.Revision, selected.Value.Agent,
                null, goal.Value, requested, RemoteOptions: remote,
                CardId: selected.Value.SourceKind == GuildAgentSourceKind.Remote ? null : selected.Value.Card);

            return new PreparedAward(context, child, null, null);
        }, ct).ConfigureAwait(false);

        if (preparation.IsFailure) return preparation.Error;

        if (preparation.Value.Replay is { } replay)
        {
            if (toolAdmission is not null && preparation.Value.ReplayQuest is { } replayQuest)
            {
                Result projection = toolAdmission.ValidateProjectionAllocation(JsonSerializer.SerializeToElement(replay, GuildJsonContext.Default.WorkAwardDto));

                if (projection.IsFailure) return projection.Error;

                toolAdmission.RecordCommittedProjection(replayQuest, JsonSerializer.SerializeToElement(replay, GuildJsonContext.Default.WorkAwardDto));
            }

            return replay;
        }

        if (preparation.Value.Context is not { } issue || preparation.Value.Child is not { } childRequest) return Unavailable;

        return await quests.IssueAwardAsync(caller, issue, childRequest, toolAdmission, ct).ConfigureAwait(false);
    }

    internal sealed record AwardCandidate(Guid Member, Guid PrimaryQuest, Guid Agent, Guid Card, long CardRevision,
        GuildAgentSourceKind SourceKind, string GrantJson, bool BudgetHint);

    internal static async Task<Result<AwardCandidate>> ReadAwardCandidateWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, WorkOfferDto offer, Guid bidId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT b.MemberId,r.PrimaryQuestId,r.AgentId,r.CardId,r.CardRevision,c.SourceKindCode,CASE WHEN typeof(o.RequestedGrantJson)='text' AND length(CAST(o.RequestedGrantJson AS BLOB))<=65536 THEN o.RequestedGrantJson END,
                c.Available,c.CurrentRevision,c.AgentId,o.BudgetHint,
                EXISTS(SELECT 1 FROM guild_agent_card_skills k WHERE k.CardId=r.CardId AND k.Revision=r.CardRevision AND k.SkillId=o.SkillId)
            FROM guild_work_bids b
            JOIN guild_work_offers o ON o.Id=b.OfferId
            JOIN guild_work_offer_recipients r ON r.OfferId=b.OfferId AND r.MemberId=b.MemberId
            JOIN guild_cohort_members m ON m.Id=r.MemberId AND m.CohortId=o.CohortId AND m.StateCode=1
                AND m.AgentId=r.AgentId AND m.PrimaryVotingQuestId=r.PrimaryQuestId
            JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=m.PrimaryVotingQuestId
                AND a.AssociationKindCode=1 AND a.Active=1
            LEFT JOIN guild_agent_cards c ON c.CardId=r.CardId
            WHERE b.Id=$bid AND b.OfferId=$offer AND b.StateCode=1 AND b.AgentId=r.AgentId AND b.CardRevision=r.CardRevision
            """);

        Add(command, "$bid", Id(bidId));
        Add(command, "$offer", Id(offer.Id));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Forbidden;

        if (reader.IsDBNull(5) || reader.IsDBNull(7) || reader.IsDBNull(8) || reader.IsDBNull(9)
            || reader.GetInt64(7) != 1 || reader.GetInt64(8) != reader.GetInt64(4)
            || !string.Equals(reader.GetString(9), reader.GetString(2), StringComparison.Ordinal) || reader.GetInt64(11) != 1)
            return new Error(ErrorCodes.Guild.DestinationChanged, "The published recipient Card has changed.");

        if (!Guid.TryParse(reader.GetString(0), out Guid member) || !Guid.TryParse(reader.GetString(1), out Guid primary)
            || !Guid.TryParse(reader.GetString(2), out Guid agent) || !Guid.TryParse(reader.GetString(3), out Guid card)
            || reader.GetValue(4) is not long revision || revision < 1 || reader.GetValue(5) is not long kind
            || !Enum.IsDefined((GuildAgentSourceKind)kind) || reader.GetValue(6) is not string grant
            || System.Text.Encoding.UTF8.GetByteCount(grant) > 65_536 || reader.GetValue(10) is not long hint || hint is not (0 or 1)) return Unavailable;

        return new AwardCandidate(member, primary, agent, card, revision, (GuildAgentSourceKind)kind, grant, hint == 1);
    }

    internal static async Task<Result<WorkAwardDto>> ReadAwardWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid awardId, CancellationToken ct)
    {
        await using var command = Command(connection, transaction,
            "SELECT OfferId,BidId,QuestId,MemberId FROM guild_work_awards WHERE Id=$id");

        Add(command, "$id", Id(awardId));

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)
            || !Guid.TryParse(reader.GetString(0), out Guid offer) || !Guid.TryParse(reader.GetString(1), out Guid bid)
            || !Guid.TryParse(reader.GetString(2), out Guid quest) || !Guid.TryParse(reader.GetString(3), out Guid member)) return Unavailable;

        return new WorkAwardDto(awardId, offer, bid, quest, member, GuildWorkAwardState.Committed);
    }
}
