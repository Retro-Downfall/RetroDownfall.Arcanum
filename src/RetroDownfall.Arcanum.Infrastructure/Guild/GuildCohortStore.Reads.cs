using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildCohortSelectedPage(ImmutableArray<CohortContributionDto> Items,
    long? NextAdmissionSequence, long ClosedMembershipVersion);

internal sealed partial class GuildCohortStore
{
    internal static async Task<Result<GuildDataRef?>> ReadSingleSelectedReferenceWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, CohortDto cohort, CancellationToken ct)
    {
        if (cohort.Policy.Kind == GuildConvergenceKind.All) return Result<GuildDataRef?>.Success(null);

        if (cohort.ClosedMembershipVersion is not { } closed) return Unavailable;

        try
        {
            var retainedRule = await ReadSelectionRuleWithinAsync(connection, transaction, cohort.Id, ct).ConfigureAwait(false);

            if (retainedRule.IsFailure) return retainedRule.Error;

            SelectionRule selection = retainedRule.Value;

            long? canonical = selection.Kind == GuildConvergenceKind.First ? selection.Through : selection.Bucket;

            if (selection.Generation < 1 || selection.Kind != cohort.Policy.Kind || canonical is null or < 1) return Unavailable;

            SelectedEntry entry;

            await using (SqliteCommand selected = SelectedQuery(connection, transaction, selection, cohort.Id, closed, 0, 1, false, canonical))
            {
                await using SqliteDataReader reader = await selected.ExecuteReaderAsync(ct).ConfigureAwait(false);

                if (!await reader.ReadAsync(ct).ConfigureAwait(false) || Enumerable.Range(0, 15).Any(reader.IsDBNull)) return Unavailable;

                entry = ParseSelectedEntry(reader);
            }

            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, entry.Session, entry.Campaign, ct).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            var source = await ReadSelectedContentWithinAsync(connection, transaction, entry, entry.SourceContent,
                GuildContentKind.QuestResult, entry.SourceRevision, entry.SourceMime, entry.SourceBytes, entry.SourceDigest, false, ct).ConfigureAwait(false);

            if (source.IsFailure) return source.Error;

            return await ReadSelectedContentWithinAsync(connection, transaction, entry, entry.Content,
                GuildContentKind.CohortContribution, entry.Revision, cohort.ResultContract.MimeType, entry.Bytes, null,
                true, ct, source.Value is not null).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or FormatException or InvalidCastException or OverflowException)
        {
            return Unavailable;
        }
    }

    internal Task<Result<GuildCohortSelectedPage>> ReadSelectedContributionsPageAsync(GuildCallerContext caller,
        Guid cohortId, long afterAdmissionSequence, int pageSize, CancellationToken ct)
    {
        if (caller?.Kind != GuildCallerKind.Run || caller.Execution is not { } execution || cohortId == Guid.Empty
            || afterAdmissionSequence < 0 || pageSize is < 1 or > 100)
            return Task.FromResult(Result<GuildCohortSelectedPage>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<GuildCohortSelectedPage>(execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var live = await signets.VerifyAsync(execution, token).ConfigureAwait(false);

                if (live.IsFailure) return live.Error;

                var found = await ReadWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                if (found.IsFailure) return found.Error;

                CohortDto cohort = found.Value.Cohort;

                if (cohort.RootQuestId != execution.RootQuestId || cohort.ClosedMembershipVersion is not { } closed
                    || cohort.State is GuildCohortState.Open or GuildCohortState.Closed) return Forbidden;

                var issuer = await GuildQuestStore.ReadWithinAsync(connection, transaction, cohort.IssuerQuestId, token).ConfigureAwait(false);

                if (issuer.IsFailure) return issuer.Error;

                if (issuer.Value.Quest.RootQuestId != cohort.RootQuestId) return Forbidden;

                Result visible = await quests.RequireVisibleAsync(connection, transaction, caller, issuer.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                try
                {
                    var retainedRule = await ReadSelectionRuleWithinAsync(connection, transaction, cohortId, token).ConfigureAwait(false);

                    if (retainedRule.IsFailure) return retainedRule.Error;

                    SelectionRule selection = retainedRule.Value;

                    if (selection.Generation < 1 || selection.Kind != cohort.Policy.Kind
                        || (selection.Kind switch
                        {
                            GuildConvergenceKind.All => selection.Bucket is not null || selection.Through is not null,
                            GuildConvergenceKind.First => selection.Bucket is not null || selection.Through is null or < 1,
                            GuildConvergenceKind.Quorum => selection.Bucket is null or < 1 || selection.Through is null
                                || selection.Through < selection.Bucket,
                            _ => true,
                        })) return Unavailable;

                    List<SelectedEntry> entries = [];

                    long? lastScanned = null;

                    await using (SqliteCommand page = SelectedQuery(connection, transaction, selection, cohortId, closed,
                        afterAdmissionSequence, 100, false))
                    {
                        await using SqliteDataReader reader = await page.ExecuteReaderAsync(token).ConfigureAwait(false);

                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            if (reader.IsDBNull(17) || reader.IsDBNull(18) || reader.IsDBNull(19) || reader.IsDBNull(20)) return Unavailable;

                            lastScanned = reader.GetInt64(17);

                            bool present = reader.GetInt64(19) == 1;

                            if (present && reader.GetInt64(20) != 1) return Unavailable;

                            bool selected = reader.GetInt64(21) == 1;

                            if (reader.GetInt64(18) != 1)
                            {
                                if (selected) return Unavailable;

                                continue;
                            }

                            if (!present)
                            {
                                if (selection.Kind == GuildConvergenceKind.All
                                    || !reader.IsDBNull(22) && reader.GetInt64(22) <= selection.Through) return Unavailable;

                                continue;
                            }

                            if (!selected) continue;

                            if (Enumerable.Range(0, 15).Any(reader.IsDBNull)) return Unavailable;

                            entries.Add(ParseSelectedEntry(reader));

                            if (entries.Count == pageSize) break;
                        }
                    }

                    ImmutableArray<CohortContributionDto>.Builder items = ImmutableArray.CreateBuilder<CohortContributionDto>(entries.Count);

                    foreach (SelectedEntry entry in entries)
                    {
                        if (entry.Member == Guid.Empty || entry.Quest == Guid.Empty || entry.Result == Guid.Empty
                            || entry.Content == Guid.Empty || entry.SourceContent == Guid.Empty
                            || entry.Session == Guid.Empty || entry.Campaign == Guid.Empty) return Unavailable;

                        Result owners = await GuildStore.RequireOwnerWithinAsync(connection, transaction,
                            entry.Session, entry.Campaign, token).ConfigureAwait(false);

                        if (owners.IsFailure) return owners.Error;

                        var source = await ReadSelectedContentWithinAsync(connection, transaction, entry,
                            entry.SourceContent, GuildContentKind.QuestResult, entry.SourceRevision, entry.SourceMime,
                            entry.SourceBytes, entry.SourceDigest, false, token).ConfigureAwait(false);

                        if (source.IsFailure) return source.Error;

                        var value = await ReadSelectedContentWithinAsync(connection, transaction, entry,
                            entry.Content, GuildContentKind.CohortContribution, entry.Revision, cohort.ResultContract.MimeType,
                            entry.Bytes, null, true, token, source.Value is not null).ConfigureAwait(false);

                        if (value.IsFailure) return value.Error;

                        GuildDataRef? content = source.Value is null ? null : value.Value;

                        items.Add(new(cohortId, entry.Member, entry.Quest, entry.Sequence, GuildContributionOutcome.Accepted,
                            content, content is null ? null : Convert.ToHexStringLower(entry.Agreement), true));
                    }

                    long? next = null;

                    if (lastScanned is { } last)
                    {
                        await using SqliteCommand more = SelectedQuery(connection, transaction, selection, cohortId, closed, last, 1, true);

                        if (await more.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) next = last;
                    }

                    return new GuildCohortSelectedPage(items.MoveToImmutable(), next, closed);
                }
                catch (Exception exception) when (exception is SqliteException or FormatException or InvalidCastException or OverflowException)
                {
                    return Unavailable;
                }
            }, ct);
    }

    private static SqliteCommand SelectedQuery(SqliteConnection connection, SqliteTransaction transaction, SelectionRule selection,
        Guid cohortId, long closed, long after, int size, bool probe, long? representative = null)
    {
        if (probe)
        {
            SqliteCommand continuation = Command(connection, transaction, """
                SELECT 1 FROM guild_cohort_members INDEXED BY idx_guild_cohort_members_order
                WHERE CohortId=$cohort AND AdmissionSequence>$after AND AdmissionSequence<=$closed LIMIT 1
                """);

            Add(continuation, "$cohort", Id(cohortId));
            Add(continuation, "$closed", closed);
            Add(continuation, "$after", after);

            return continuation;
        }

        const string fields = """
            CASE WHEN length(m.Id)=36 AND m.AdmissionSequence BETWEEN 1 AND $closed
                AND (m.RemovalSequence IS NULL OR m.RemovalSequence>$closed)
                AND typeof(candidate.Generation)='integer' AND candidate.Generation=$generation
                AND typeof(candidate.ResultSequence)='integer' AND candidate.ResultSequence>=1
                AND typeof(candidate.OutcomeCode)='integer' AND candidate.OutcomeCode=1
                AND (candidate.BucketId IS NULL OR typeof(candidate.BucketId)='integer' AND candidate.BucketId>=1)
                AND v.ResultSequence=candidate.ResultSequence AND v.OutcomeCode=1
                AND q.Id IS NOT NULL AND q.Retired=0 AND q.RootQuestId=c.RootQuestId AND a.QuestId IS NOT NULL
                AND (q.SessionId IS NULL OR typeof(q.SessionId)='text' AND length(CAST(q.SessionId AS BLOB))=36)
                AND (q.CampaignId IS NULL OR typeof(q.CampaignId)='text' AND length(CAST(q.CampaignId AS BLOB))=36) THEN m.Id END,
            CASE WHEN length(v.PrimaryVotingQuestId)=36 THEN v.PrimaryVotingQuestId END,
            CASE WHEN typeof(m.AdmissionSequence)='integer' AND m.AdmissionSequence>=1 THEN m.AdmissionSequence END,
            CASE WHEN typeof(v.ResultSequence)='integer' AND v.ResultSequence>=1 THEN v.ResultSequence END,
            CASE WHEN length(v.ResultId)=36 THEN v.ResultId END,
            CASE WHEN length(v.ContentId)=36 THEN v.ContentId END,
            CASE WHEN typeof(v.ContentRevision)='integer' AND v.ContentRevision>=1 THEN v.ContentRevision END,
            CASE WHEN typeof(v.ProofDigest)='blob' AND length(v.ProofDigest)=32 AND v.ProofDigest=o.ProofDigest THEN v.ProofDigest END,
            CASE WHEN typeof(v.AgreementDigest)='blob' AND length(v.AgreementDigest)=32 THEN v.AgreementDigest END,
            CASE WHEN typeof(v.ByteLength)='integer' AND v.ByteLength BETWEEN 1 AND 65536 THEN v.ByteLength END,
            CASE WHEN length(r.ContentId)=36 THEN r.ContentId END,
            CASE WHEN typeof(r.ContentRevision)='integer' AND r.ContentRevision>=1 THEN r.ContentRevision END,
            CASE WHEN typeof(r.Digest)='blob' AND length(r.Digest)=32 THEN r.Digest END,
            CASE WHEN r.MimeType IN('text/plain','application/json') THEN r.MimeType END,
            CASE WHEN typeof(r.ByteLength)='integer' AND r.ByteLength BETWEEN 0 AND 65536 THEN r.ByteLength END,
            CASE WHEN q.SessionId IS NULL OR length(q.SessionId)=36 THEN q.SessionId END,
            CASE WHEN q.CampaignId IS NULL OR length(q.CampaignId)=36 THEN q.CampaignId END,
            CASE WHEN typeof(m.AdmissionSequence)='integer' AND m.AdmissionSequence>=1 THEN m.AdmissionSequence END,
            CASE WHEN m.RemovalSequence IS NULL THEN 1 WHEN typeof(m.RemovalSequence)='integer'
                THEN CASE WHEN m.RemovalSequence>$closed THEN 1 ELSE 0 END END,
            candidate.MemberId IS NOT NULL,
            CASE WHEN candidate.MemberId IS NULL THEN 1 ELSE
                typeof(candidate.Generation)='integer' AND candidate.Generation=$generation
                AND typeof(candidate.ResultSequence)='integer' AND candidate.ResultSequence>=1
                AND typeof(candidate.OutcomeCode)='integer' AND candidate.OutcomeCode BETWEEN 1 AND 6
                AND (candidate.BucketId IS NULL OR typeof(candidate.BucketId)='integer' AND candidate.BucketId>=1)
                AND ($kind<>3 OR candidate.OutcomeCode<>1 OR candidate.BucketId IS NOT NULL) END,
            CASE WHEN candidate.OutcomeCode=1 AND ($kind=1 OR $kind=2 AND candidate.ResultSequence=$through
                OR $kind=3 AND candidate.BucketId=$bucket AND candidate.ResultSequence<=$through) THEN 1 ELSE 0 END,
            CASE WHEN typeof(v.ResultSequence)='integer' AND v.ResultSequence>=1 THEN v.ResultSequence END
            """;

        string origin = representative is null ? """
            FROM guild_cohort_members m INDEXED BY idx_guild_cohort_members_order
            LEFT JOIN guild_cohort_eval_candidates candidate ON candidate.CohortId=m.CohortId
                AND candidate.Generation=$generation AND candidate.MemberId=m.Id
            """ : """
            FROM guild_cohort_eval_candidates candidate
            LEFT JOIN guild_cohort_members m ON m.Id=candidate.MemberId AND m.CohortId=candidate.CohortId
            """;

        string range = representative is null
            ? "m.CohortId=$cohort AND m.AdmissionSequence>$after AND m.AdmissionSequence<=$closed ORDER BY m.AdmissionSequence"
            : "candidate.CohortId=$cohort AND candidate.Generation=$generation AND candidate.ResultSequence=$representative AND candidate.OutcomeCode=1";

        SqliteCommand command = Command(connection, transaction, $"""
            SELECT {fields} {origin}
            LEFT JOIN guild_cohort_contributions v ON v.CohortId=m.CohortId AND v.MemberId=m.Id
                AND v.PrimaryVotingQuestId=m.PrimaryVotingQuestId
            LEFT JOIN guild_cohort_member_quests a ON a.MemberId=m.Id AND a.QuestId=m.PrimaryVotingQuestId AND a.AssociationKindCode=1
            LEFT JOIN guild_quests q ON q.Id=m.PrimaryVotingQuestId AND q.AgentId=m.AgentId
            LEFT JOIN guild_cohorts c ON c.Id=m.CohortId
            LEFT JOIN guild_result_receipts r ON r.QuestId=q.Id AND r.ResultId=v.ResultId AND r.OutcomeCode=3
            LEFT JOIN guild_cohort_result_observations o ON o.QuestId=q.Id AND o.ResultId=r.ResultId
            WHERE {range} LIMIT $take
            """);

        Add(command, "$cohort", Id(cohortId));
        Add(command, "$generation", selection.Generation);
        Add(command, "$closed", closed);
        Add(command, "$after", after);
        Add(command, "$kind", (int)selection.Kind);
        Add(command, "$through", selection.Through);
        Add(command, "$bucket", selection.Bucket);
        Add(command, "$take", size);
        Add(command, "$representative", representative);

        return command;
    }

    private static async Task<Result<SelectionRule>> ReadSelectionRuleWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid cohortId, CancellationToken ct)
    {
        await using SqliteCommand rule = Command(connection, transaction, """
            SELECT Generation,KindCode,WinningBucket,ThroughSequence,
                CASE WHEN typeof(Generation)='integer' AND Generation>=1 AND typeof(KindCode)='integer' AND KindCode IN(1,2,3)
                    AND (WinningBucket IS NULL OR typeof(WinningBucket)='integer' AND WinningBucket>=1)
                    AND (ThroughSequence IS NULL OR typeof(ThroughSequence)='integer' AND ThroughSequence>=1)
                    AND ((KindCode=1 AND WinningBucket IS NULL AND ThroughSequence IS NULL)
                        OR (KindCode=2 AND WinningBucket IS NULL AND ThroughSequence IS NOT NULL)
                        OR (KindCode=3 AND WinningBucket IS NOT NULL AND ThroughSequence>=WinningBucket)) THEN 1 ELSE 0 END
            FROM guild_cohort_selections WHERE CohortId=$cohort
            """);

        Add(rule, "$cohort", Id(cohortId));

        await using SqliteDataReader reader = await rule.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt64(4) != 1) return Unavailable;

        return new SelectionRule(reader.GetInt64(0), (GuildConvergenceKind)reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));
    }

    private sealed record SelectionRule(long Generation, GuildConvergenceKind Kind, long? Bucket, long? Through);

    private static SelectedEntry ParseSelectedEntry(SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2), reader.GetInt64(3),
            Guid.Parse(reader.GetString(4)), Guid.Parse(reader.GetString(5)), reader.GetInt64(6),
            (byte[])reader.GetValue(7), (byte[])reader.GetValue(8), reader.GetInt64(9),
            reader.IsDBNull(15) ? null : Guid.Parse(reader.GetString(15)), reader.IsDBNull(16) ? null : Guid.Parse(reader.GetString(16)),
            Guid.Parse(reader.GetString(10)), reader.GetInt64(11), (byte[])reader.GetValue(12), reader.GetString(13), reader.GetInt64(14));

    private sealed record SelectedEntry(Guid Member, Guid Quest, long Admission, long Sequence, Guid Result,
        Guid Content, long Revision, byte[] Proof, byte[] Agreement, long Bytes, Guid? Session, Guid? Campaign,
        Guid SourceContent, long SourceRevision, byte[] SourceDigest, string SourceMime, long SourceBytes);
}
