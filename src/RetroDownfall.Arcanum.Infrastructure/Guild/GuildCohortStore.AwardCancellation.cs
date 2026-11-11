using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    /// <summary>Retained cursors alternate bounded primary and offer checks, including expiry without a revocation event.</summary>
    private async Task<Result<bool>> ScanAwardAuthorityWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        SignetAuthority.QuestTransaction signets, CohortDto cohort, CancellationToken ct)
    {
        if (cohort.State is not (GuildCohortState.Open or GuildCohortState.Closed)) return false;

        await using (var initialize = Command(connection, transaction,
            "INSERT INTO guild_cohort_award_authority_scan(CohortId) VALUES($cohort) ON CONFLICT(CohortId) DO NOTHING"))
        {
            Add(initialize, "$cohort", Id(cohort.Id));

            await initialize.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int phase;
        long memberAfter;
        long offerAfter;
        long? savedSnapshot;

        await using (var checkpoint = Command(connection, transaction,
            "SELECT PhaseCode,LastMemberSequence,LastOfferSequence,SnapshotSequence FROM guild_cohort_award_authority_scan WHERE CohortId=$cohort"))
        {
            Add(checkpoint, "$cohort", Id(cohort.Id));

            await using var reader = await checkpoint.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)
                || reader.GetValue(0) is not long code || code is not (1 or 2)
                || reader.GetValue(1) is not long members || members < 0
                || reader.GetValue(2) is not long offers || offers < 0) return Unavailable;

            phase = (int)code;
            memberAfter = members;
            offerAfter = offers;
            savedSnapshot = reader.IsDBNull(3) ? null : reader.GetValue(3) is long high && high >= 0 ? high : -1;

            if (savedSnapshot == -1) return Unavailable;
        }

        long snapshot = savedSnapshot ?? cohort.MembershipVersion;

        if (savedSnapshot is null && phase == 2)
        {
            await using var high = Command(connection, transaction, "SELECT OfferSequence FROM guild_cohorts WHERE Id=$cohort");

            Add(high, "$cohort", Id(cohort.Id));

            if (await high.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long value || value < 0) return Unavailable;

            snapshot = value;
        }

        long last = phase == 1 ? memberAfter : offerAfter;

        if (last > snapshot) return Unavailable;

        List<(Guid Id, Guid Quest, long Sequence, long Revision)> rows = [];

        await using (var page = Command(connection, transaction, phase == 1 ? """
            SELECT CASE WHEN length(CAST(m.Id AS BLOB))=36 THEN m.Id END,
                CASE WHEN length(CAST(m.PrimaryVotingQuestId AS BLOB))=36 THEN m.PrimaryVotingQuestId END,
                m.AdmissionSequence,0
            FROM guild_cohort_members m JOIN guild_quests q ON q.Id=m.PrimaryVotingQuestId
            WHERE m.CohortId=$cohort AND m.StateCode=1 AND m.AdmissionSequence>$after AND m.AdmissionSequence<=$snapshot
                AND q.RootQuestId=$root AND q.AgentId=m.AgentId
                AND EXISTS(SELECT 1 FROM guild_cohort_member_quests a WHERE a.MemberId=m.Id AND a.AssociationKindCode=2)
            ORDER BY m.AdmissionSequence,m.Id LIMIT 100
            """ : """
            SELECT CASE WHEN length(CAST(Id AS BLOB))=36 THEN Id END,
                CASE WHEN length(CAST(IssuerQuestId AS BLOB))=36 THEN IssuerQuestId END,OfferSequence,Revision
            FROM guild_work_offers WHERE CohortId=$cohort AND StateCode=1 AND OfferSequence>$after AND OfferSequence<=$snapshot
            ORDER BY OfferSequence,Id LIMIT 100
            """))
        {
            Add(page, "$cohort", Id(cohort.Id));
            Add(page, "$root", Id(cohort.RootQuestId));
            Add(page, "$after", last);
            Add(page, "$snapshot", snapshot);

            await using var reader = await page.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)
                    || !Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty
                    || !Guid.TryParseExact(reader.GetString(1), "D", out Guid quest) || quest == Guid.Empty
                    || reader.GetValue(2) is not long sequence || sequence <= last
                    || reader.GetValue(3) is not long revision || revision < (phase == 1 ? 0 : 1)) return Unavailable;

                rows.Add((id, quest, sequence, revision));
                last = sequence;
            }
        }

        bool changed = false;
        bool closedOffers = false;

        foreach (var row in rows)
        {
            var live = await signets.ValidateCurrentGrantAsync(row.Quest, ct).ConfigureAwait(false);

            bool denied = live.IsFailure && IsUnavailableAwardAuthority(live.Error);

            if (live.IsFailure && !denied) return live.Error;

            if (phase == 2 && !denied)
            {
                var issuer = await ReadIssuerFactsWithinAsync(connection, transaction, row.Quest, ct).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                if (issuer.Value.RootQuestId != cohort.RootQuestId) return Unavailable;

                denied = issuer.Value.Stopped;

                if (!denied)
                {
                    var owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction,
                        issuer.Value.SessionId, issuer.Value.CampaignId, ct).ConfigureAwait(false);

                    denied = owner.IsFailure && owner.Error.Code == ErrorCodes.Guild.OwnerUnavailable;

                    if (owner.IsFailure && !denied) return owner.Error;
                }
            }

            if (!denied) continue;

            if (phase == 1)
            {
                await using var stop = Command(connection, transaction, """
                    INSERT INTO guild_cohort_stop_frontiers(Id,CohortId,MemberId) VALUES($id,$cohort,$member)
                    ON CONFLICT(CohortId,MemberId) DO NOTHING
                    """);

                Add(stop, "$id", Id(Guid.NewGuid()));
                Add(stop, "$cohort", Id(cohort.Id));
                Add(stop, "$member", Id(row.Id));

                changed |= await stop.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            }
            else
            {
                if (row.Revision == long.MaxValue) return Unavailable;

                await using var close = Command(connection, transaction, """
                    UPDATE guild_work_offers SET StateCode=4,Revision=Revision+1,ClosedReason='issuer_unavailable'
                    WHERE Id=$offer AND CohortId=$cohort AND StateCode=1 AND Revision=$revision
                    """);

                Add(close, "$offer", Id(row.Id));
                Add(close, "$cohort", Id(cohort.Id));
                Add(close, "$revision", row.Revision);

                if (await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;

                changed = true;
                closedOffers = true;
            }
        }

        bool complete = rows.Count < 100;
        int nextPhase = complete ? (phase == 1 ? 2 : 1) : phase;
        long nextMember = phase == 1 ? complete ? 0 : last : memberAfter;
        long nextOffer = phase == 2 ? complete ? 0 : last : offerAfter;

        await using (var advance = Command(connection, transaction, """
            UPDATE guild_cohort_award_authority_scan SET PhaseCode=$nextPhase,LastMemberSequence=$nextMember,LastOfferSequence=$nextOffer,SnapshotSequence=$snapshot
            WHERE CohortId=$cohort AND PhaseCode=$phase AND LastMemberSequence=$member AND LastOfferSequence=$offer AND SnapshotSequence IS $priorSnapshot
            """))
        {
            Add(advance, "$nextPhase", nextPhase);
            Add(advance, "$snapshot", complete ? null : snapshot);
            Add(advance, "$priorSnapshot", savedSnapshot);
            Add(advance, "$nextMember", nextMember);
            Add(advance, "$nextOffer", nextOffer);
            Add(advance, "$cohort", Id(cohort.Id));
            Add(advance, "$phase", phase);
            Add(advance, "$member", memberAfter);
            Add(advance, "$offer", offerAfter);

            if (await advance.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;
        }

        if (closedOffers)
        {
            if (cohort.Revision == long.MaxValue) return Unavailable;

            var ev = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId, cohort.IssuerQuestId,
                GuildEventKind.CohortChanged, cohort.Id, cohort.Revision + 1, null, time.GetUtcNow(), ct).ConfigureAwait(false);

            if (ev.IsFailure) return ev.Error;

            await using var update = Command(connection, transaction,
                "UPDATE guild_cohorts SET Revision=Revision+1,EventWatermark=$event WHERE Id=$cohort AND Revision=$revision");

            Add(update, "$event", ev.Value);
            Add(update, "$cohort", Id(cohort.Id));
            Add(update, "$revision", cohort.Revision);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;
        }

        return changed;
    }

    private static bool IsUnavailableAwardAuthority(Error error) => error.Code is ErrorCodes.Guild.AuthorityRevoked
        or ErrorCodes.Guild.AuthorityExpired or ErrorCodes.Guild.OwnerUnavailable;
}
