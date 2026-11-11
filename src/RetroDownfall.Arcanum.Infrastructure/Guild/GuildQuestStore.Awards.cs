using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private sealed record AwardAdmission(GuildGrant PrimaryCeiling, WorkAwardDto? Replay);

    private async Task<Result<AwardAdmission>> ValidateAwardIssueWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildAwardIssue issue, GuildIssueChildRequest child, CancellationToken ct)
    {
        if (issue.RootQuestId == Guid.Empty || issue.IssuerQuestId == Guid.Empty || issue.Request.IdempotencyKey != issue.Mutation.IdempotencyKey
            || issue.Request.BidId != issue.BidId || issue.Request.ExpectedRevision != issue.ExpectedOfferRevision
            || issue.Request.ExpectedCohortRevision != issue.ExpectedCohortRevision || child.IdempotencyKey != issue.Request.IdempotencyKey)
            return InvalidReceipt;

        if (caller.Kind == GuildCallerKind.Run)
        {
            if (caller.Execution?.QuestId != issue.IssuerQuestId || caller.Execution.RootQuestId != issue.RootQuestId) return InvalidReceipt;
            var live = await signets.VerifyAsync(caller.Execution, ct).ConfigureAwait(false);
            if (live.IsFailure) return live.Error;
        }
        else if (caller.Kind == GuildCallerKind.Operator)
        {
            var live = await signets.ValidateCurrentGrantAsync(issue.IssuerQuestId, ct).ConfigureAwait(false);
            if (live.IsFailure) return live.Error;
        }
        else return InvalidReceipt;

        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(issue.Request, GuildJsonContext.Default.AwardWorkRequest));
        int kind = caller.Kind == GuildCallerKind.Operator ? 1 : 2;
        Guid principal = kind == 1 ? caller.PrincipalId!.Value : issue.IssuerQuestId;

        await using (var prior = Command(connection, transaction, """
            SELECT ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc,RequestDigest FROM guild_work_mutations
            WHERE PrincipalKindCode=$kind AND PrincipalId=$principal AND ActionCode=5 AND ParentId=$offer AND IdempotencyKey=$key
            """))
        {
            Add(prior, "$kind", kind);
            Add(prior, "$principal", Id(principal));
            Add(prior, "$offer", Id(issue.OfferId));
            Add(prior, "$key", Id(issue.Request.IdempotencyKey));

            await using var reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetValue(4) is not byte[] previous || !CryptographicOperations.FixedTimeEquals(previous, digest))
                    return new Error(ErrorCodes.Guild.IdempotencyConflict, "The award identity was accepted with different input.");

                Guid id = Guid.Parse(reader.GetString(0));
                Guid receipt = Guid.Parse(reader.GetString(1));
                long revision = reader.GetInt64(2);
                DateTimeOffset at = GuildMarketStorage.ParseReceiptInstant(reader.GetString(3));
                await reader.DisposeAsync().ConfigureAwait(false);

                var replay = await GuildCohortStore.ReadAwardWithinAsync(connection, transaction, id, ct).ConfigureAwait(false);
                return replay.IsFailure ? replay.Error : new AwardAdmission(issue.PrimaryCeiling, replay.Value with
                {
                    Receipt = new(receipt, issue.Request.IdempotencyKey, true, at, revision),
                });
            }
        }

        var cohort = await GuildCohortStore.ReadWithinAsync(connection, transaction, issue.CohortId, ct).ConfigureAwait(false);
        if (cohort.IsFailure) return cohort.Error;

        if (cohort.Value.Cohort.RootQuestId != issue.RootQuestId || cohort.Value.Cohort.State != GuildCohortState.Open
            || cohort.Value.Cohort.Revision != issue.ExpectedCohortRevision) return GuildCohortStore.RevisionConflict(cohort.Value.Cohort);

        var offer = await GuildCohortStore.ReadOfferWithinAsync(connection, transaction, issue.OfferId, ct).ConfigureAwait(false);
        if (offer.IsFailure) return offer.Error;

        if (offer.Value.CohortId != issue.CohortId || offer.Value.IssuerQuestId != issue.IssuerQuestId
            || offer.Value.State != GuildWorkOfferState.Open || offer.Value.Revision != issue.ExpectedOfferRevision) return RevisionConflict;

        var candidate = await GuildCohortStore.ReadAwardCandidateWithinAsync(connection, transaction, offer.Value, issue.BidId, ct).ConfigureAwait(false);
        if (candidate.IsFailure) return candidate.Error;

        if (candidate.Value.Member != issue.MemberId || candidate.Value.PrimaryQuest != issue.PrimaryQuestId
            || candidate.Value.Agent != issue.AgentId || candidate.Value.Card != issue.CardId || candidate.Value.CardRevision != issue.CardRevision
            || candidate.Value.BudgetHint != issue.BudgetHint
            || child.AgentId != issue.AgentId || (candidate.Value.SourceKind == GuildAgentSourceKind.Remote ? child.CardId is not null : child.CardId != issue.CardId)) return Conflict;

        if (candidate.Value.SourceKind == GuildAgentSourceKind.Remote)
        {
            if (child.RemoteOptions is not { Mode: GuildRemoteDispatchMode.Blocking } options
                || options.SkillId != offer.Value.SkillId || options.AcceptedOutputModes.Length != 1
                || options.AcceptedOutputModes[0] != offer.Value.ResultContract.MimeType) return InvalidReceipt;
        }
        else if (child.RemoteOptions is not null) return InvalidReceipt;

        string expectedGrant = JsonSerializer.Serialize(child.RequestedGrant, GuildJsonContext.Default.GuildRequestedGrant);
        if (candidate.Value.GrantJson != expectedGrant) return Conflict;

        var goal = await ReadProviderContentWithinAsync(connection, transaction, offer.Value.GoalRef, ct).ConfigureAwait(false);
        if (goal.IsFailure) return goal.Error;
        if (goal.Value != child.Goal) return Conflict;

        var primary = await ReadWithinAsync(connection, transaction, issue.PrimaryQuestId, ct).ConfigureAwait(false);
        if (primary.IsFailure) return primary.Error;
        if (primary.Value.Retired || primary.Value.Quest.CancellationRequested
            || primary.Value.Quest.State is not (QuestState.Submitted or QuestState.Working or QuestState.InputRequired)) return InvalidReceipt;

        var primarySignet = await GuildSignetStore.ReadAsync(connection, transaction, issue.PrimaryQuestId, true, ct).ConfigureAwait(false);
        if (primarySignet.IsFailure) return primarySignet.Error;

        var primaryLive = await signets.ValidateCurrentGrantAsync(issue.PrimaryQuestId, ct).ConfigureAwait(false);
        if (primaryLive.IsFailure) return primaryLive.Error;

        if (primarySignet.Value.Binding.SignetId != issue.PrimarySignetId || primarySignet.Value.RevocationGeneration != issue.PrimaryRevocationGeneration
            || primarySignet.Value.Binding.AgentId != issue.AgentId || primarySignet.Value.Binding.RootQuestId != issue.RootQuestId) return InvalidReceipt;

        var issuer = await ReadWithinAsync(connection, transaction, issue.IssuerQuestId, ct).ConfigureAwait(false);
        if (issuer.IsFailure) return issuer.Error;

        await using (var envelope = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_budget_accounts a JOIN guild_quests creator ON creator.Id=$creator
                WHERE a.AccountId=$envelope AND a.OwnerKindCode=1 AND a.OwnerId=$cohort
                    AND a.ParentAccountId=creator.AllocationAccountId AND a.Closed=0)
            """))
        {
            Add(envelope, "$creator", Id(cohort.Value.Cohort.IssuerQuestId));
            Add(envelope, "$envelope", Id(cohort.Value.Cohort.AllocationAccountId));
            Add(envelope, "$cohort", Id(issue.CohortId));
            if (!Equals(1L, await envelope.ExecuteScalarAsync(ct).ConfigureAwait(false))) return InvalidReceipt;
        }

        if (cohort.Value.Cohort.IssuerQuestId != issue.IssuerQuestId)
        {
            await using var ancestry = Command(connection, transaction, """
                WITH RECURSIVE ancestors(Id) AS (
                    SELECT $account UNION SELECT a.ParentAccountId FROM guild_budget_accounts a JOIN ancestors p ON a.AccountId=p.Id WHERE a.ParentAccountId IS NOT NULL
                ) SELECT EXISTS(SELECT 1 FROM ancestors WHERE Id=$envelope)
                    AND EXISTS(SELECT 1 FROM guild_cohort_members m JOIN guild_cohort_member_quests a ON a.MemberId=m.Id
                        WHERE m.CohortId=$cohort AND m.StateCode=1 AND a.Active=1 AND a.QuestId=$issuer AND m.AgentId=$agent)
                """);
            Add(ancestry, "$account", Id(issuer.Value.AllocationAccountId));
            Add(ancestry, "$envelope", Id(cohort.Value.Cohort.AllocationAccountId));
            Add(ancestry, "$cohort", Id(issue.CohortId));
            Add(ancestry, "$issuer", Id(issue.IssuerQuestId));
            Add(ancestry, "$agent", Id(issuer.Value.Quest.AgentId));
            if (!Equals(1L, await ancestry.ExecuteScalarAsync(ct).ConfigureAwait(false))) return InvalidReceipt;
        }

        return new AwardAdmission(primaryLive.Value, null);
    }

    private async Task<Result<WorkAwardDto>> CommitAwardWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, GuildCallerContext caller, GuildAwardIssue issue,
        GuildQuestStored work, DateTimeOffset now, CancellationToken ct)
    {
        if (work.Quest.ParentQuestId != issue.IssuerQuestId || work.Quest.RootQuestId != issue.RootQuestId
            || work.Quest.AgentId != issue.AgentId || work.Quest.CardId != issue.CardId || work.Quest.CardRevision != issue.CardRevision)
            return new Error(ErrorCodes.Guild.DestinationChanged, "The published recipient Card has changed.");

        long associationSequence;

        await using (var sequence = Command(connection, transaction,
            "SELECT COALESCE(MAX(AssociationSequence),0) FROM guild_cohort_member_quests WHERE MemberId=$member"))
        {
            Add(sequence, "$member", Id(issue.MemberId));
            if (await sequence.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long current || current == long.MaxValue) return Unavailable;
            associationSequence = current + 1;
        }

        Guid awardId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();

        await using (var association = Command(connection, transaction, """
            INSERT INTO guild_cohort_member_quests(MemberId,QuestId,AssociationKindCode,AssociationSequence,Active)
            SELECT $member,$quest,2,$sequence,1 FROM guild_cohort_members m JOIN guild_quests q ON q.Id=$quest
            WHERE m.Id=$member AND m.CohortId=$cohort AND m.StateCode=1 AND m.AgentId=q.AgentId AND q.ParentQuestId=$issuer
            """))
        {
            Add(association, "$member", Id(issue.MemberId));
            Add(association, "$quest", Id(work.Quest.Id));
            Add(association, "$sequence", associationSequence);
            Add(association, "$cohort", Id(issue.CohortId));
            Add(association, "$issuer", Id(issue.IssuerQuestId));
            if (await association.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return InvalidReceipt;
        }

        await using (var insert = Command(connection, transaction, """
            INSERT INTO guild_work_awards(Id,OfferId,BidId,QuestId,MemberId,PrimaryQuestId,PrimarySignetId,PrimaryRevocationGeneration)
            VALUES($id,$offer,$bid,$quest,$member,$primary,$signet,$generation)
            """))
        {
            Add(insert, "$id", Id(awardId));
            Add(insert, "$offer", Id(issue.OfferId));
            Add(insert, "$bid", Id(issue.BidId));
            Add(insert, "$quest", Id(work.Quest.Id));
            Add(insert, "$member", Id(issue.MemberId));
            Add(insert, "$primary", Id(issue.PrimaryQuestId));
            Add(insert, "$signet", Id(issue.PrimarySignetId));
            Add(insert, "$generation", issue.PrimaryRevocationGeneration);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var update = Command(connection, transaction, """
            UPDATE guild_work_offers SET StateCode=2,Revision=Revision+1,AwardId=$award,QuestId=$quest
            WHERE Id=$offer AND StateCode=1 AND Revision=$revision
            """))
        {
            Add(update, "$award", Id(awardId));
            Add(update, "$quest", Id(work.Quest.Id));
            Add(update, "$offer", Id(issue.OfferId));
            Add(update, "$revision", issue.ExpectedOfferRevision);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return RevisionConflict;
        }

        var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, issue.RootQuestId,
            issue.IssuerQuestId, GuildEventKind.AwardCommitted, awardId, 1, null, now, ct).ConfigureAwait(false);
        if (projected.IsFailure) return projected.Error;

        await using (var cohort = Command(connection, transaction, """
            UPDATE guild_cohorts SET Revision=Revision+1,EventWatermark=$event
            WHERE Id=$cohort AND StateCode=1 AND Revision=$revision AND Revision<9223372036854775807
            """))
        {
            Add(cohort, "$event", projected.Value);
            Add(cohort, "$cohort", Id(issue.CohortId));
            Add(cohort, "$revision", issue.ExpectedCohortRevision);
            if (await cohort.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return RevisionConflict;
        }

        int kind = caller.Kind == GuildCallerKind.Operator ? 1 : 2;
        Guid principal = kind == 1 ? caller.PrincipalId!.Value : issue.IssuerQuestId;
        byte[] digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(issue.Request, GuildJsonContext.Default.AwardWorkRequest));

        await using (var accepted = Command(connection, transaction, """
            INSERT INTO guild_work_mutations(PrincipalKindCode,PrincipalId,ActionCode,ParentId,IdempotencyKey,RequestDigest,ResultId,ReceiptId,AcceptedRevision,AcceptedAtUtc)
            VALUES($kind,$principal,5,$parent,$key,$digest,$result,$receipt,1,$now)
            """))
        {
            Add(accepted, "$kind", kind);
            Add(accepted, "$principal", Id(principal));
            Add(accepted, "$parent", Id(issue.OfferId));
            Add(accepted, "$key", Id(issue.Request.IdempotencyKey));
            Add(accepted, "$digest", digest);
            Add(accepted, "$result", Id(awardId));
            Add(accepted, "$receipt", Id(receiptId));
            Add(accepted, "$now", UtcInstantText.Format(now));
            await accepted.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var cohortRecord = await GuildCohortStore.ReadWithinAsync(connection, transaction, issue.CohortId, ct).ConfigureAwait(false);
        if (cohortRecord.IsFailure) return cohortRecord.Error;
        var issuer = await ReadWithinAsync(connection, transaction, issue.IssuerQuestId, ct).ConfigureAwait(false);
        if (issuer.IsFailure) return issuer.Error;

        var offer = await GuildCohortStore.ReadOfferWithinAsync(connection, transaction, issue.OfferId, ct).ConfigureAwait(false);
        if (offer.IsFailure) return offer.Error;

        var portal = new GuildPortalStore(this, authority, policy, time);
        Result notification = await portal.PublishDistributionWithinAsync(connection, transaction, signets, caller,
            issuer.Value, cohortRecord.Value.Cohort, new("work.awarded", issue.CohortId, awardId, offer.Value.OfferSequence),
            Guid.NewGuid(), issue.MemberId, now, ct, issue.PrimaryQuestId).ConfigureAwait(false);
        if (notification.IsFailure) return notification.Error;

        return new WorkAwardDto(awardId, issue.OfferId, issue.BidId, work.Quest.Id, issue.MemberId,
            GuildWorkAwardState.Committed, new(receiptId, issue.Request.IdempotencyKey, false, now, 1));
    }
}
