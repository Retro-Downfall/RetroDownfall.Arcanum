using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private sealed record OperatorContribution(Guid Member, Guid Quest, long Sequence, GuildContributionOutcome Outcome,
        Guid? Result, Guid? Content, long? Revision, byte[]? Proof, byte[]? Agreement, long? Bytes, bool Selected);

    internal async Task<Result<CohortContributionPageDto>> ListOperatorContributionsAsync(GuildCallerContext caller, Guid cohortId,
        bool? selected, GuildPageQuery query, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || cohortId == Guid.Empty || query is null || query.PageSize is < 1 or > 100)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The contribution page is invalid.");
        string cursorScope = "contributions:" + Id(cohortId) + ":" + (selected is null ? "*" : selected.Value ? "1" : "0")
            + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);
        Result<Guid> root = query.Cursor is null ? await NoteRootAsync(caller, cohortId, ct).ConfigureAwait(false)
            : await guild.AuthenticateOperatorCursorAsync(caller, policy, query.Cursor, NoteReaderKey(caller), cursorScope, true, ct).ConfigureAwait(false);
        if (root.IsFailure) return root.Error;
        Result<CohortContributionPageDto> pageResult = await authority.InQuestTransactionAsync<CohortContributionPageDto>(root.Value, async (connection, transaction, _, token) =>
        {
            Result<GuildCohortStored> found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);
            if (found.IsFailure) return found.Error;
            CohortDto cohort = found.Value.Cohort;
            if (cohort.RootQuestId != root.Value) return Forbidden;
            Result<GuildQuestStored> issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);
            if (issuer.IsFailure) return issuer.Error;
            Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);
            if (visible.IsFailure) return visible.Error;
            string scope = "contributions:" + Id(cohortId) + ":" + (selected is null ? "*" : selected.Value ? "1" : "0")
                + ":" + query.PageSize.ToString(CultureInfo.InvariantCulture);
            string callerKey = NoteReaderKey(caller);
            Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);
            if (epoch.IsFailure) return epoch.Error;
            Result<string> visibility = await GuildOperatorVisibility.ReadAsync(connection, transaction, policy, token).ConfigureAwait(false);
            if (visibility.IsFailure) return visibility.Error;
            long after = 0;
            long snapshot;
            long selectionGeneration;
            if (query.Cursor is { } cursor)
            {
                if (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out Guid id) || id == Guid.Empty) return NoteCursorInvalid;
                await using SqliteCommand saved = Command(connection, transaction, """
                    SELECT InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,VisibilityKey,AfterSequence,SnapshotSequence
                    FROM guild_portal_cursors WHERE Id=$id
                    """);
                Add(saved, "$id", Id(id));
                await using SqliteDataReader reader = await saved.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return NoteCursorInvalid;
                if (reader.GetString(2) != callerKey || reader.GetString(3) != Id(root.Value)) return NoteCursorInvalid;
                string savedScope = reader.GetString(4);
                string prefix = scope + ":";
                if (!savedScope.StartsWith(prefix, StringComparison.Ordinal)
                    || !long.TryParse(savedScope.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out selectionGeneration)
                    || selectionGeneration < 0) return NoteCursorInvalid;
                if (reader.GetString(0) != Id(policy.InstallationGeneration) || reader.GetString(1) != Id(epoch.Value)
                    || reader.GetString(5) != GuildOperatorVisibility.Bind(Id(cohortId), visibility.Value)) return NoteCursorGone;
                if (reader.GetValue(6) is not long position || reader.GetValue(7) is not long high || position < 0 || high < position) return Unavailable;
                after = position; snapshot = high;
            }
            else
            {
                await using SqliteCommand high = Command(connection, transaction, "SELECT ResultSequence FROM guild_cohorts WHERE Id=$cohort");
                Add(high, "$cohort", Id(cohortId));
                if (await high.ExecuteScalarAsync(token).ConfigureAwait(false) is not long value || value < 0) return Unavailable;
                snapshot = value;
                await using SqliteCommand selection = Command(connection, transaction,
                    "SELECT COALESCE((SELECT Generation FROM guild_cohort_selections WHERE CohortId=$cohort),0)");
                Add(selection, "$cohort", Id(cohortId));
                if (await selection.ExecuteScalarAsync(token).ConfigureAwait(false) is not long generation || generation < 0) return Unavailable;
                selectionGeneration = generation;
            }

            List<OperatorContribution> rows = [];
            await using (SqliteCommand select = Command(connection, transaction, """
                SELECT v.MemberId,v.PrimaryVotingQuestId,v.ResultSequence,v.OutcomeCode,v.ResultId,v.ContentId,v.ContentRevision,
                    v.ProofDigest,v.AgreementDigest,v.ByteLength,
                    EXISTS(SELECT 1 FROM guild_cohort_selections s JOIN guild_cohort_eval_candidates candidate
                        ON candidate.CohortId=s.CohortId AND candidate.Generation=s.Generation
                        WHERE s.CohortId=v.CohortId AND s.Generation=$selection AND candidate.MemberId=v.MemberId AND candidate.ResultSequence=v.ResultSequence
                            AND candidate.OutcomeCode=1 AND (s.KindCode=1 OR s.KindCode=2 AND candidate.ResultSequence=s.ThroughSequence
                                OR s.KindCode=3 AND candidate.BucketId=s.WinningBucket AND candidate.ResultSequence<=s.ThroughSequence)) AS Selected
                FROM guild_cohort_contributions v WHERE v.CohortId=$cohort AND v.ResultSequence>$after AND v.ResultSequence<=$snapshot
                ORDER BY v.ResultSequence,v.MemberId LIMIT $limit
                """))
            {
                Add(select, "$cohort", Id(cohortId)); Add(select, "$selection", selectionGeneration); Add(select, "$after", after); Add(select, "$snapshot", snapshot); Add(select, "$limit", query.PageSize + 1);
                await using SqliteDataReader reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!Guid.TryParse(reader.GetString(0), out Guid member) || member == Guid.Empty
                        || !Guid.TryParse(reader.GetString(1), out Guid quest) || quest == Guid.Empty
                        || reader.GetValue(2) is not long sequence || sequence < 1 || reader.GetValue(3) is not long outcome || outcome is < 1 or > 6) return Unavailable;
                    Guid? result = null;
                    Guid? content = null;
                    if (!reader.IsDBNull(4))
                    {
                        if (!Guid.TryParse(reader.GetString(4), out Guid id) || id == Guid.Empty) return Unavailable;
                        result = id;
                    }

                    if (!reader.IsDBNull(5))
                    {
                        if (!Guid.TryParse(reader.GetString(5), out Guid id) || id == Guid.Empty) return Unavailable;
                        content = id;
                    }
                    rows.Add(new(member, quest, sequence, (GuildContributionOutcome)outcome, result, content,
                        reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : (byte[])reader.GetValue(7),
                        reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8), reader.IsDBNull(9) ? null : reader.GetInt64(9), reader.GetInt64(10) == 1));
                }
            }
            ImmutableArray<CohortContributionDto>.Builder items = ImmutableArray.CreateBuilder<CohortContributionDto>();
            long last = after;
            bool more = false;
            foreach (OperatorContribution row in rows)
            {
                if (selected is { } only && row.Selected != only) { last = row.Sequence; continue; }
                if (items.Count == query.PageSize) { more = true; break; }
                Result<GuildQuestStored> quest = await GuildQuestStore.ReadWithinAsync(connection, transaction, row.Quest, token).ConfigureAwait(false);
                if (quest.IsFailure) return quest.Error;
                Result allowed = await quests.RequireVisibleAsync(connection, transaction, caller, quest.Value, token).ConfigureAwait(false);
                if (allowed.IsFailure) return allowed.Error;
                GuildDataRef? content = null;
                string? agreement = null;
                if (row.Outcome == GuildContributionOutcome.Accepted)
                {
                    if (row.Result is not { } resultId || row.Content is not { } contentId || row.Revision is not { } revision
                        || row.Bytes is not { } bytes || row.Proof is not { Length: 32 } proof || row.Agreement is not { Length: 32 } agreementDigest) return Unavailable;
                    Result<GuildResultSourceState> source = await ReadCurrentResultSourceStateWithinAsync(connection, transaction, row.Quest, resultId, token).ConfigureAwait(false);
                    if (source.IsFailure) return source.Error;
                    Result<GuildDataRef?> reference = await ReadCurrentContentWithinAsync(connection, transaction, resultId,
                        quest.Value.Quest.SessionId, quest.Value.Quest.CampaignId, contentId, GuildContentKind.CohortContribution,
                        revision, cohort.ResultContract.MimeType, bytes, null, proof, agreementDigest, token, source.Value == GuildResultSourceState.Clean).ConfigureAwait(false);
                    if (reference.IsFailure) return reference.Error;
                    content = reference.Value;
                    agreement = content is null ? null : Convert.ToHexStringLower(agreementDigest);
                }
                items.Add(new(cohortId, row.Member, row.Quest, row.Sequence, row.Outcome, content, agreement, row.Selected));
                last = row.Sequence;
            }

            if (!more && rows.Count > query.PageSize) more = true;
            string? next = null;
            if (more && last > after)
            {
                Guid cursorId = Guid.NewGuid();
                await using SqliteCommand insert = Command(connection, transaction, """
                    INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                        AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                    VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$visibility)
                    """);
                Add(insert, "$id", Id(cursorId)); Add(insert, "$generation", Id(policy.InstallationGeneration)); Add(insert, "$epoch", Id(epoch.Value));
                Add(insert, "$caller", callerKey); Add(insert, "$root", Id(root.Value)); Add(insert, "$scope", scope + ":" + selectionGeneration.ToString(CultureInfo.InvariantCulture));
                Add(insert, "$after", last); Add(insert, "$snapshot", snapshot); Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));
                Add(insert, "$visibility", GuildOperatorVisibility.Bind(Id(cohortId), visibility.Value));
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                next = cursorId.ToString("N");
            }

            return new CohortContributionPageDto(items.ToImmutable(), next, snapshot);
        }, ct).ConfigureAwait(false);
        return query.Cursor is not null && pageResult.IsFailure && pageResult.Error.Code is ErrorCodes.Guild.OwnerUnavailable or ErrorCodes.Guild.NotFound
            ? NoteCursorGone : pageResult;
    }
}
