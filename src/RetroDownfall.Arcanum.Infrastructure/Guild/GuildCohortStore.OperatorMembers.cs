using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal async Task<Result<CohortMemberPageDto>> GetOperatorMemberPageAsync(GuildCallerContext caller, Guid cohortId,
        GuildPageQuery query, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || query is null || cohortId == Guid.Empty)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The member page is invalid.");

        string scope = "members:" + Id(cohortId) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);

        Result<Guid> root = query.Cursor is null ? await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false)
            : await guild.AuthenticateOperatorCursorAsync(caller, policy, query.Cursor, NoteReaderKey(caller), scope, false, ct).ConfigureAwait(false);

        if (root.IsFailure) return root.Error;

        Result<CohortMemberPageDto> pageResult = await authority.InQuestTransactionAsync<CohortMemberPageDto>(root.Value,
            (connection, transaction, _, token) => GetOperatorMemberPageWithinAsync(connection, transaction, caller, cohortId, query, token), ct).ConfigureAwait(false);

        return query.Cursor is not null && pageResult.IsFailure && pageResult.Error.Code is ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound
            ? NoteCursorGone : pageResult;
    }

    internal async Task<Result<CohortMemberPageDto>> GetOperatorMemberPageWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildCallerContext caller, Guid cohortId, GuildPageQuery query, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || cohortId == Guid.Empty || query is null || query.PageSize is < 1 or > 100)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The member page is invalid.");

        string scope = "members:" + Id(cohortId) + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);

        string callerKey = NoteReaderKey(caller);

        Guid cursorId = Guid.Empty;

        if (query.Cursor is { } cursor && (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out cursorId) || cursorId == Guid.Empty))
            return NoteCursorInvalid;

        Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, ct).ConfigureAwait(false);

        if (found.IsFailure) return query.Cursor is not null && found.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? NoteCursorGone : found.Error;

        CohortDto cohort = found.Value.Cohort;

        Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction, cohort.IssuerQuestId, null, null, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error.Code == ErrorCodes.Guild.Forbidden ? NoteCursorGone : located.Error;

        Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, ct).ConfigureAwait(false);

        if (issuer.IsFailure) return issuer.Error;

        Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, ct).ConfigureAwait(false);

        if (visible.IsFailure) return visible.Error;

        if (issuer.Value.Quest.RootQuestId != cohort.RootQuestId) return Forbidden;

        Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure) return epoch.Error;

        Result<string> visibility = await GuildOperatorVisibility.ReadAsync(connection, transaction, policy, ct).ConfigureAwait(false);

        if (visibility.IsFailure) return visibility.Error;

        long after = 0;

        long snapshot = cohort.MembershipVersion;

        if (query.Cursor is not null)
        {
            await using SqliteCommand saved = Command(connection, transaction, """
                SELECT InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,VisibilityKey,AfterSequence,SnapshotSequence
                FROM guild_portal_cursors WHERE Id=$id
                """);

            Add(saved, "$id", Id(cursorId));

            await using SqliteDataReader reader = await saved.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return NoteCursorInvalid;

            if (reader.GetString(2) != callerKey || reader.GetString(3) != Id(cohort.RootQuestId)
                || reader.GetString(4) != scope) return NoteCursorInvalid;

            if (reader.GetString(5) != GuildOperatorVisibility.Bind(Id(cohortId), visibility.Value)) return NoteCursorGone;

            if (reader.GetString(0) != Id(policy.InstallationGeneration) || reader.GetString(1) != Id(epoch.Value)) return NoteCursorGone;

            if (reader.GetValue(6) is not long position || reader.GetValue(7) is not long high || position < 1 || high < position) return Unavailable;

            after = position;

            snapshot = high;
        }

        List<Guid> ids = [];

        await using (SqliteCommand queryMembers = Command(connection, transaction, """
            SELECT Id FROM guild_cohort_members WHERE CohortId=$cohort AND AdmissionSequence>$after AND AdmissionSequence<=$snapshot
            ORDER BY AdmissionSequence,Id LIMIT $limit
            """))
        {
            Add(queryMembers, "$cohort", Id(cohortId));
            Add(queryMembers, "$after", after);
            Add(queryMembers, "$snapshot", snapshot);
            Add(queryMembers, "$limit", query.PageSize + 1);

            await using SqliteDataReader reader = await queryMembers.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out Guid id) || id == Guid.Empty) return Unavailable;

                ids.Add(id);
            }
        }

        bool more = ids.Count > query.PageSize;

        ImmutableArray<CohortMemberDto>.Builder members = ImmutableArray.CreateBuilder<CohortMemberDto>();

        foreach (Guid id in ids.Take(query.PageSize))
        {
            Result<CohortMemberDto> member = await ReadMemberWithinAsync(connection, transaction, cohort, id, ct).ConfigureAwait(false);

            if (member.IsFailure) return member.Error;

            Result<GuildQuestStored> quest = await GuildQuestStore.ReadWithinAsync(connection, transaction, member.Value.PrimaryVotingQuestId, ct).ConfigureAwait(false);

            if (quest.IsFailure) return quest.Error;

            Result canRead = await quests.RequireVisibleAsync(connection, transaction, caller, quest.Value, ct).ConfigureAwait(false);

            if (canRead.IsFailure) return canRead.Error;

            members.Add(member.Value with { Receipt = null });
        }

        string? next = null;

        if (more && members.Count > 0)
        {
            Guid id = Guid.NewGuid();

            await using SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                    AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$cohort)
                """);

            Add(insert, "$id", Id(id));
            Add(insert, "$generation", Id(policy.InstallationGeneration));
            Add(insert, "$epoch", Id(epoch.Value));
            Add(insert, "$caller", callerKey);
            Add(insert, "$root", Id(cohort.RootQuestId));
            Add(insert, "$scope", scope);
            Add(insert, "$after", members[^1].AdmissionSequence);
            Add(insert, "$snapshot", snapshot);
            Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));
            Add(insert, "$cohort", GuildOperatorVisibility.Bind(Id(cohortId), visibility.Value));

            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            next = id.ToString("N");
        }

        return new CohortMemberPageDto(members.ToImmutable(), next, snapshot);
    }
}
