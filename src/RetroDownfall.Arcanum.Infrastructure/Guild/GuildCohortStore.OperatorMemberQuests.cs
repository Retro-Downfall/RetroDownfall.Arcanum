using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal async Task<Result<CohortMemberQuestPageDto>> GetOperatorMemberQuestsAsync(GuildCallerContext caller,
        Guid cohortId, Guid memberId, GuildPageQuery query, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || cohortId == Guid.Empty || memberId == Guid.Empty
            || query is null || query.PageSize is < 1 or > 100)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The member Quest page is invalid.");

        string cursorScope = "member-quests:" + Id(cohortId) + ":" + Id(memberId) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);
        Result<Guid> root = query.Cursor is null ? await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false)
            : await guild.AuthenticateOperatorCursorAsync(caller, policy, query.Cursor, NoteReaderKey(caller), cursorScope, false, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;

        Result<CohortMemberQuestPageDto> pageResult = await authority.InQuestTransactionAsync<CohortMemberQuestPageDto>(root.Value, async (connection, transaction, _, token) =>
        {
            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            if (found.IsFailure) return found.Error;
            CohortDto cohort = found.Value.Cohort;
            if (cohort.RootQuestId != root.Value) return Forbidden;
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            Result<CohortMemberDto> member = await ReadMemberWithinAsync(connection, transaction, cohort, memberId, token).ConfigureAwait(false);
            if (member.IsFailure) return new Error(ErrorCodes.Guild.NotFound, "The Cohort member is unavailable.");
            string scope = "member-quests:" + Id(cohortId) + ":" + Id(memberId) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);
            string callerKey = NoteReaderKey(caller);
            Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);
            if (epoch.IsFailure) return epoch.Error;
            Result<string> visibility = await GuildOperatorVisibility.ReadAsync(connection, transaction, policy, token).ConfigureAwait(false);
            if (visibility.IsFailure) return visibility.Error;
            long after = 0;
            long snapshot;
            if (query.Cursor is { } cursor)
            {
                if (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out Guid cursorId) || cursorId == Guid.Empty) return NoteCursorInvalid;
                await using SqliteCommand saved = Command(connection, transaction, """
                    SELECT InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,VisibilityKey,AfterSequence,SnapshotSequence
                    FROM guild_portal_cursors WHERE Id=$id
                    """);
                Add(saved, "$id", Id(cursorId));
                await using SqliteDataReader reader = await saved.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return NoteCursorInvalid;
                if (reader.GetString(2) != callerKey || reader.GetString(3) != Id(root.Value) || reader.GetString(4) != scope) return NoteCursorInvalid;
                if (reader.GetString(0) != Id(policy.InstallationGeneration) || reader.GetString(1) != Id(epoch.Value)
                    || reader.GetString(5) != GuildOperatorVisibility.Bind(Id(memberId), visibility.Value)) return NoteCursorGone;
                if (reader.GetValue(6) is not long position || reader.GetValue(7) is not long high || position < 0 || high < position) return Unavailable;
                after = position; snapshot = high;
            }
            else
            {
                await using SqliteCommand high = Command(connection, transaction,
                    "SELECT COALESCE(MAX(AssociationSequence),0) FROM guild_cohort_member_quests WHERE MemberId=$member");
                Add(high, "$member", Id(memberId));
                if (await high.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value < 0) return Unavailable;
                snapshot = value;
            }

            List<(Guid Quest, long Sequence, GuildMemberAssociationKind Kind)> rows = [];
            await using (SqliteCommand select = Command(connection, transaction, """
                SELECT QuestId,AssociationSequence,AssociationKindCode FROM guild_cohort_member_quests
                WHERE MemberId=$member AND AssociationSequence>$after AND AssociationSequence<=$snapshot
                ORDER BY AssociationSequence,QuestId LIMIT $limit
                """))
            {
                Add(select, "$member", Id(memberId)); Add(select, "$after", after); Add(select, "$snapshot", snapshot);
                Add(select, "$limit", query.PageSize + 1);
                await using SqliteDataReader reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!Guid.TryParse(reader.GetString(0), out Guid quest) || quest == Guid.Empty || reader.GetValue(1) is not long sequence
                        || reader.GetValue(2) is not long kind || kind is not (1 or 2)) return Unavailable;
                    rows.Add((quest, sequence, (GuildMemberAssociationKind)kind));
                }
            }

            bool more = rows.Count > query.PageSize;
            ImmutableArray<CohortMemberQuestDto>.Builder items = ImmutableArray.CreateBuilder<CohortMemberQuestDto>();
            foreach (var row in rows.Take(query.PageSize))
            {
                Result<GuildQuestStored> quest = await GuildQuestStore.ReadWithinAsync(connection, transaction, row.Quest, token).ConfigureAwait(false);
                if (quest.IsFailure) return quest.Error;
                Result allowed = await quests.RequireVisibleAsync(connection, transaction, caller, quest.Value, token).ConfigureAwait(false);
                if (allowed.IsFailure) return allowed.Error;
                items.Add(new(memberId, row.Quest, row.Kind, row.Sequence));
            }

            string? next = null;
            if (more && items.Count > 0)
            {
                Guid id = Guid.NewGuid();
                await using SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                        AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                    VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$visibility)
                    """);
                Add(insert, "$id", Id(id)); Add(insert, "$generation", Id(policy.InstallationGeneration)); Add(insert, "$epoch", Id(epoch.Value));
                Add(insert, "$caller", callerKey); Add(insert, "$root", Id(root.Value)); Add(insert, "$scope", scope);
                Add(insert, "$after", items[^1].AssociationSequence); Add(insert, "$snapshot", snapshot);
                Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(insert, "$visibility", GuildOperatorVisibility.Bind(Id(memberId), visibility.Value));
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                next = id.ToString("N");
            }

            return new CohortMemberQuestPageDto(items.ToImmutable(), next, snapshot);
        }, ct).ConfigureAwait(false);
        return query.Cursor is not null && pageResult.IsFailure && pageResult.Error.Code is ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound
            ? NoteCursorGone : pageResult;
    }
}
