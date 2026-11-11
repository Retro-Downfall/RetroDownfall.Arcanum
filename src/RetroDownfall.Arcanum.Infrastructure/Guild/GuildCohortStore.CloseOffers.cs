using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    /// <summary>Close affected immutable offers under the caller's existing root transaction.</summary>
    internal static async Task<Result> CloseOffersWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohortId, Guid? memberId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        while (true)
        {
            Guid offer;
            Guid issuer;
            Guid root;
            long revision;

            await using (var next = Command(connection, transaction, """
                SELECT o.Id,o.IssuerQuestId,c.RootQuestId,o.Revision FROM guild_work_offers o
                JOIN guild_cohorts c ON c.Id=o.CohortId
                WHERE o.CohortId=$cohort AND o.StateCode=1 AND ($member IS NULL OR EXISTS(
                    SELECT 1 FROM guild_cohort_member_quests a WHERE a.MemberId=$member AND a.QuestId=o.IssuerQuestId))
                ORDER BY o.OfferSequence,o.Id LIMIT 1
                """))
            {
                Add(next, "$cohort", Id(cohortId));
                Add(next, "$member", memberId is { } member ? Id(member) : null);
                await using var reader = await next.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result.Success();
                if (!Guid.TryParse(reader.GetString(0), out offer) || !Guid.TryParse(reader.GetString(1), out issuer)
                    || !Guid.TryParse(reader.GetString(2), out root) || reader.GetValue(3) is not long value || value < 1 || value == long.MaxValue)
                    return Unavailable;
                revision = value;
            }

            await using (var close = Command(connection, transaction, """
                UPDATE guild_work_offers SET StateCode=4,Revision=Revision+1,ClosedReason=$reason
                WHERE Id=$offer AND StateCode=1 AND Revision=$revision
                """))
            {
                Add(close, "$reason", reason);
                Add(close, "$offer", Id(offer));
                Add(close, "$revision", revision);
                if (await close.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;
            }

            var projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, root, issuer,
                GuildEventKind.OfferChanged, offer, revision + 1, null, now, ct).ConfigureAwait(false);
            if (projected.IsFailure) return projected.Error;
        }
    }
}
