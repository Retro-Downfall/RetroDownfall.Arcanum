using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    private sealed record EvaluationClock(long Membership, long Results, long Frozen, long Content, long Sensitivity, long Owners, string Visibility);

    private sealed record EvaluationState(long Generation, long Last, long Processed, long Invalid, long Maximum,
        long? Pending, long CollisionAfter);

    private sealed record EvaluationCandidate(long Sequence, Guid Member, GuildContributionOutcome Outcome,
        Guid? Content, byte[]? Bytes, byte[]? Agreement, bool Eligible = true);

    private sealed record EvaluationBucket(long Id, byte[] Body, long Count);

    /// <summary>Advances at most 100 source/bucket rows; every decision and its cancellation frontier commit together.</summary>
    internal static async Task<Result<bool>> EvaluateWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildCohortStored cohort, IGuildQuestHostPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        if (cohort.Cohort.State != GuildCohortState.Closed) return false;

        if (cohort.Cohort.ClosedMembershipVersion is not > 0) return Unavailable;

        Result<EvaluationClock> clockRead = await ReadEvaluationClockAsync(connection, transaction, cohort.Cohort.Id, policy, ct).ConfigureAwait(false);

        if (clockRead.IsFailure) return clockRead.Error;

        EvaluationClock clock = clockRead.Value;

        if (cohort.Cohort.ClosedMembershipVersion > clock.Membership) return Unavailable;

        Result<EvaluationState> loaded = await LoadEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, clock, ct).ConfigureAwait(false);

        if (loaded.IsFailure) return loaded.Error;

        EvaluationState state = loaded.Value;

        Result<int> retired = await RetireEvaluationGenerationsWithinAsync(connection, transaction,
            cohort.Cohort.Id, state.Generation, ct).ConfigureAwait(false);

        if (retired.IsFailure) return retired.Error;

        if (retired.Value > 0) return true;

        int work = 0;

        while (work < 100)
        {
            Result<EvaluationCandidate?> next = await ReadEvaluationCandidateAsync(connection, transaction, cohort.Cohort,
                state.Last, clock.Results, ct).ConfigureAwait(false);

            if (next.IsFailure) return next.Error;

            if (next.Value is not { } candidate)
            {
                if (state.Pending is not null || state.Last != clock.Results || state.Processed > clock.Frozen) return Unavailable;

                long pending = clock.Frozen - state.Processed;

                bool impossible = cohort.Cohort.Policy.Kind switch
                {
                    GuildConvergenceKind.All => state.Invalid > 0,
                    GuildConvergenceKind.First => pending == 0,
                    GuildConvergenceKind.Quorum => state.Maximum + pending < cohort.Cohort.Policy.Quorum!.Value,
                    _ => false,
                };

                bool allSucceeded = cohort.Cohort.Policy.Kind == GuildConvergenceKind.All && state.Invalid == 0 && pending == 0;

                if (!impossible && !allSucceeded) return false;

                return await CommitEvaluationDecisionAsync(connection, transaction, cohort.Cohort,
                    state.Generation, allSucceeded ? GuildConvergenceDecision.Success : GuildConvergenceDecision.Impossible,
                    null, null, policy, clock, now, ct).ConfigureAwait(false);
            }

            work++;

            if (state.Last == long.MaxValue || candidate.Sequence != state.Last + 1) return Unavailable;

            if (!candidate.Eligible)
            {
                if (state.Pending is not null) return Unavailable;

                state = state with { Last = candidate.Sequence };

                Result skipped = await SaveEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, state, ct).ConfigureAwait(false);

                if (skipped.IsFailure) return skipped.Error;

                continue;
            }

            if (state.Pending is { } pendingSequence && pendingSequence != candidate.Sequence) return Unavailable;

            if (state.Processed >= clock.Frozen) return Unavailable;

            if (candidate.Outcome != GuildContributionOutcome.Accepted)
            {
                if (state.Pending is not null) return Unavailable;

                Result saved = await SaveEvaluationCandidateAsync(connection, transaction, cohort.Cohort.Id,
                    state.Generation, candidate, null, ct).ConfigureAwait(false);

                if (saved.IsFailure) return saved.Error;

                state = state with { Last = candidate.Sequence, Processed = state.Processed + 1, Invalid = state.Invalid + 1 };

                Result update = await SaveEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, state, ct).ConfigureAwait(false);

                if (update.IsFailure) return update.Error;

                if (cohort.Cohort.Policy.Kind == GuildConvergenceKind.All)
                    return await CommitEvaluationDecisionAsync(connection, transaction, cohort.Cohort, state.Generation,
                        GuildConvergenceDecision.Impossible, null, null, policy, clock, now, ct).ConfigureAwait(false);

                continue;
            }

            if (candidate.Bytes is null || candidate.Agreement is null || candidate.Content is null) return Unavailable;

            if (work >= 100)
            {
                state = state with { Pending = candidate.Sequence };

                Result update = await SaveEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, state, ct).ConfigureAwait(false);

                return update.IsFailure ? update.Error : false;
            }

            Result<List<EvaluationBucket>> scanned = await ReadEvaluationBucketsAsync(connection, transaction,
                cohort.Cohort.Id, state.Generation, cohort.Cohort.ResultContract.MimeType, candidate.Agreement,
                state.CollisionAfter, Math.Min(10, 100 - work), ct).ConfigureAwait(false);

            if (scanned.IsFailure) return scanned.Error;

            work += scanned.Value.Count;

            EvaluationBucket? matching = scanned.Value.FirstOrDefault(bucket => bucket.Body.AsSpan().SequenceEqual(candidate.Bytes));

            if (matching is null && scanned.Value.Count == 10 && work < 100)
            {
                state = state with { Pending = candidate.Sequence, CollisionAfter = scanned.Value[^1].Id };

                Result update = await SaveEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, state, ct).ConfigureAwait(false);

                if (update.IsFailure) return update.Error;

                continue;
            }

            if (matching is null && work >= 100 && scanned.Value.Count > 0)
            {
                state = state with { Pending = candidate.Sequence, CollisionAfter = scanned.Value[^1].Id };

                Result update = await SaveEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, state, ct).ConfigureAwait(false);

                return update.IsFailure ? update.Error : false;
            }

            long bucketId = matching?.Id ?? candidate.Sequence;

            long count = matching is null ? 1 : matching.Count + 1;

            await using (SqliteCommand bucket = Command(connection, transaction, """
                INSERT INTO guild_cohort_eval_buckets(CohortId,Generation,BucketId,AgreementDigest,RepresentativeContentId,VoteCount)
                VALUES($cohort,$generation,$bucket,$digest,$content,1)
                ON CONFLICT(CohortId,Generation,BucketId) DO UPDATE SET VoteCount=VoteCount+1
                WHERE VoteCount=$previous AND VoteCount<9223372036854775807
                """))
            {
                Add(bucket, "$cohort", Id(cohort.Cohort.Id));
                Add(bucket, "$generation", state.Generation);
                Add(bucket, "$bucket", bucketId);
                Add(bucket, "$digest", candidate.Agreement);
                Add(bucket, "$content", Id(candidate.Content.Value));
                Add(bucket, "$previous", matching?.Count ?? 0);

                if (await bucket.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Unavailable;
            }

            Result inserted = await SaveEvaluationCandidateAsync(connection, transaction, cohort.Cohort.Id,
                state.Generation, candidate, bucketId, ct).ConfigureAwait(false);

            if (inserted.IsFailure) return inserted.Error;

            state = state with { Last = candidate.Sequence, Processed = state.Processed + 1,
                Maximum = Math.Max(state.Maximum, count), Pending = null, CollisionAfter = 0 };

            Result persisted = await SaveEvaluationStateAsync(connection, transaction, cohort.Cohort.Id, state, ct).ConfigureAwait(false);

            if (persisted.IsFailure) return persisted.Error;

            if (cohort.Cohort.Policy.Kind == GuildConvergenceKind.First
                || cohort.Cohort.Policy.Kind == GuildConvergenceKind.Quorum && count == cohort.Cohort.Policy.Quorum)
                return await CommitEvaluationDecisionAsync(connection, transaction, cohort.Cohort, state.Generation,
                    GuildConvergenceDecision.Success, cohort.Cohort.Policy.Kind == GuildConvergenceKind.Quorum ? bucketId : null,
                    candidate.Sequence, policy, clock, now, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<Result<int>> RetireEvaluationGenerationsWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid cohort, long currentGeneration, CancellationToken ct)
    {
        int removed = 0;

        foreach ((string table, string key) in new[]
        {
            ("guild_cohort_eval_candidates", "MemberId"),
            ("guild_cohort_eval_buckets", "BucketId"),
        })
        {
            if (removed == 100) break;

            // Both identifiers are fixed internally; the primary-key order keeps each page indexed.
            await using SqliteCommand command = Command(connection, transaction, $"""
                DELETE FROM {table} WHERE rowid IN (
                    SELECT old.rowid FROM {table} old
                    WHERE old.CohortId=$cohort AND old.Generation<$generation
                      AND NOT EXISTS(SELECT 1 FROM guild_cohort_selections selected
                          WHERE selected.CohortId=old.CohortId AND selected.Generation=old.Generation)
                    ORDER BY old.Generation,old.{key} LIMIT $limit)
                """);

            Add(command, "$cohort", Id(cohort));
            Add(command, "$generation", currentGeneration);
            Add(command, "$limit", 100 - removed);

            int count = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (count < 0 || count > 100 - removed) return Unavailable;

            removed += count;
        }

        return removed;
    }

    private static async Task<Result<EvaluationClock>> ReadEvaluationClockAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, IGuildQuestHostPolicy policy, CancellationToken ct)
    {
        // owner_deletion_events is a permanent journal: its update/delete guards reject mutation,
        // so its indexed maximum sequence is a monotonic ownership invalidation watermark.
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT MembershipVersion,ResultSequence,FrozenMemberCount,
                COALESCE((SELECT CASE WHEN typeof(Revision)='integer' AND Revision>=1 THEN Revision ELSE -1 END
                    FROM guild_cohort_content_clock WHERE Id=1),0),
                COALESCE((SELECT CASE WHEN typeof(Revision)='integer' AND Revision>=1 THEN Revision ELSE -1 END
                    FROM artifact_sensitivity_clock WHERE Id=1),0),
                COALESCE((SELECT MAX(Sequence) FROM owner_deletion_events),0),
                CASE WHEN typeof(MembershipVersion)='integer' AND MembershipVersion>=1
                    AND typeof(ResultSequence)='integer' AND ResultSequence>=0
                    AND typeof(FrozenMemberCount)='integer' AND FrozenMemberCount>=1 THEN 1 ELSE 0 END
            FROM guild_cohorts WHERE Id=$cohort AND StateCode=2
            """);

        Add(command, "$cohort", Id(cohort));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(2) || reader.GetInt64(6) != 1) return Unavailable;

        string visibility = policy is IGuildPortalDisclosurePolicy disclosure ? disclosure.VisibilityEpoch : "";

        if (visibility is null || System.Text.Encoding.UTF8.GetByteCount(visibility) > 65_536) return Unavailable;

        visibility = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(visibility)));

        EvaluationClock clock = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), visibility);

        return clock.Membership >= 1 && clock.Results >= 0 && clock.Frozen >= 1
            && clock.Content >= 0 && clock.Sensitivity >= 0 && clock.Owners >= 0 ? clock : Unavailable;
    }

    private static async Task<Result<EvaluationState>> LoadEvaluationStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, EvaluationClock clock, CancellationToken ct)
    {
        long generation = 1;

        await using (SqliteCommand select = Command(connection, transaction, """
            SELECT Generation,MembershipVersion,ResultSequence,ContentEpoch,SensitivityEpoch,OwnerEpoch,
                CASE WHEN typeof(VisibilityEpoch)='text' AND length(CAST(VisibilityEpoch AS BLOB))<=64 THEN VisibilityEpoch END,
                LastResultSequence,ProcessedCount,InvalidCount,MaxBucketCount,PendingResultSequence,CollisionAfterBucket,
                CASE WHEN typeof(Generation)='integer' AND typeof(MembershipVersion)='integer' AND typeof(ResultSequence)='integer'
                    AND typeof(ContentEpoch)='integer' AND typeof(SensitivityEpoch)='integer' AND typeof(OwnerEpoch)='integer'
                    AND typeof(LastResultSequence)='integer' AND typeof(ProcessedCount)='integer' AND typeof(InvalidCount)='integer'
                    AND typeof(MaxBucketCount)='integer' AND (PendingResultSequence IS NULL OR typeof(PendingResultSequence)='integer')
                    AND typeof(CollisionAfterBucket)='integer' THEN 1 ELSE 0 END
            FROM guild_cohort_eval_state WHERE CohortId=$cohort
            """))
        {
            Add(select, "$cohort", Id(cohort));

            await using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(6) || reader.GetInt64(13) != 1) return Unavailable;

                generation = reader.GetInt64(0);

                long savedResults = reader.GetInt64(2);

                bool same = reader.GetInt64(1) == clock.Membership
                    && reader.GetInt64(3) == clock.Content && reader.GetInt64(4) == clock.Sensitivity
                    && reader.GetInt64(5) == clock.Owners && reader.GetString(6) == clock.Visibility;

                if (same)
                {
                    EvaluationState state = new(generation, reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9),
                        reader.GetInt64(10), reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetInt64(12));

                    if (savedResults < 0 || savedResults > clock.Results || state.Generation < 1 || state.Last < 0 || state.Last > savedResults || state.Processed < 0
                        || state.Processed > clock.Frozen || state.Invalid < 0 || state.Invalid > state.Processed
                        || state.Maximum < 0 || state.Maximum > state.Processed || state.CollisionAfter < 0
                        || state.Pending is { } pending && (pending <= state.Last || pending > savedResults)) return Unavailable;

                    await reader.DisposeAsync().ConfigureAwait(false);

                    if (savedResults != clock.Results)
                    {
                        await using SqliteCommand extend = Command(connection, transaction, """
                            UPDATE guild_cohort_eval_state SET ResultSequence=$results
                            WHERE CohortId=$cohort AND Generation=$generation AND ResultSequence=$previous
                            """);

                        Add(extend, "$results", clock.Results);
                        Add(extend, "$cohort", Id(cohort));
                        Add(extend, "$generation", generation);
                        Add(extend, "$previous", savedResults);

                        if (await extend.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Unavailable;
                    }

                    return state;
                }

                if (generation == long.MaxValue) return Unavailable;

                generation++;
            }
        }

        await using SqliteCommand reset = Command(connection, transaction, """
            INSERT INTO guild_cohort_eval_state(CohortId,Generation,MembershipVersion,ResultSequence,ContentEpoch,SensitivityEpoch,
                OwnerEpoch,VisibilityEpoch,LastResultSequence,ProcessedCount,InvalidCount,MaxBucketCount,PendingResultSequence,CollisionAfterBucket)
            VALUES($cohort,$generation,$membership,$results,$content,$sensitivity,$owners,$visibility,0,0,0,0,NULL,0)
            ON CONFLICT(CohortId) DO UPDATE SET Generation=excluded.Generation,MembershipVersion=excluded.MembershipVersion,
                ResultSequence=excluded.ResultSequence,ContentEpoch=excluded.ContentEpoch,SensitivityEpoch=excluded.SensitivityEpoch,
                OwnerEpoch=excluded.OwnerEpoch,VisibilityEpoch=excluded.VisibilityEpoch,LastResultSequence=0,ProcessedCount=0,
                InvalidCount=0,MaxBucketCount=0,PendingResultSequence=NULL,CollisionAfterBucket=0
            """);

        Add(reset, "$cohort", Id(cohort));
        Add(reset, "$generation", generation);
        Add(reset, "$membership", clock.Membership);
        Add(reset, "$results", clock.Results);
        Add(reset, "$content", clock.Content);
        Add(reset, "$sensitivity", clock.Sensitivity);
        Add(reset, "$owners", clock.Owners);
        Add(reset, "$visibility", clock.Visibility);

        return await reset.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1
            ? new EvaluationState(generation, 0, 0, 0, 0, null, 0) : Unavailable;
    }

    private static async Task<Result> SaveEvaluationStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, EvaluationState state, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            UPDATE guild_cohort_eval_state SET LastResultSequence=$last,ProcessedCount=$processed,InvalidCount=$invalid,
                MaxBucketCount=$maximum,PendingResultSequence=$pending,CollisionAfterBucket=$collision
            WHERE CohortId=$cohort AND Generation=$generation
            """);

        Add(command, "$cohort", Id(cohort));
        Add(command, "$generation", state.Generation);
        Add(command, "$last", state.Last);
        Add(command, "$processed", state.Processed);
        Add(command, "$invalid", state.Invalid);
        Add(command, "$maximum", state.Maximum);
        Add(command, "$pending", state.Pending);
        Add(command, "$collision", state.CollisionAfter);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Unavailable;
    }

    private static async Task<Result> SaveEvaluationCandidateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid cohort, long generation, EvaluationCandidate candidate, long? bucket, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_cohort_eval_candidates(CohortId,Generation,MemberId,ResultSequence,OutcomeCode,BucketId)
            VALUES($cohort,$generation,$member,$sequence,$outcome,$bucket)
            """);

        Add(command, "$cohort", Id(cohort));
        Add(command, "$generation", generation);
        Add(command, "$member", Id(candidate.Member));
        Add(command, "$sequence", candidate.Sequence);
        Add(command, "$outcome", (int)candidate.Outcome);
        Add(command, "$bucket", bucket);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Unavailable;
    }

    private static async Task<Result<EvaluationCandidate?>> ReadEvaluationCandidateAsync(SqliteConnection connection, SqliteTransaction transaction,
        CohortDto cohort, long after, long snapshot, CancellationToken ct)
    {
        try
        {
            await using SqliteCommand command = Command(connection, transaction, """
                SELECT v.ResultSequence,v.MemberId,v.OutcomeCode,v.ContentId,v.ContentRevision,NULL,v.ByteLength,v.ResultId,
                    q.SessionId,q.CampaignId,c.ContentKindCode,c.SourceRecordId,c.Revision,NULL,c.MimeType,length(c.Body),
                    c.SensitivityCode,c.ProvenanceModeCode,
                    NULL,
                    EXISTS(SELECT 1 FROM artifact_sensitivity l WHERE l.ArtifactKindCode=17
                        AND lower(replace(l.ArtifactId,'-',''))=lower(replace(v.ContentId,'-',''))),
                    EXISTS(SELECT 1 FROM guild_content_erasure_receipts e WHERE e.ContentId=v.ContentId
                        AND e.ContentKindCode=$contentKind AND e.SourceRecordId=v.ResultId AND e.Revision=v.ContentRevision
                        AND e.ByteLength=v.ByteLength AND e.MimeType=$mime AND e.SessionId IS q.SessionId AND e.CampaignId IS q.CampaignId),
                    CASE WHEN typeof(v.ResultSequence)='integer' AND v.ResultSequence>=1
                      AND typeof(v.MemberId)='text' AND length(CAST(v.MemberId AS BLOB))=36
                      AND typeof(v.OutcomeCode)='integer' AND v.OutcomeCode BETWEEN 1 AND 6
                      AND typeof(m.AdmissionSequence)='integer' AND m.AdmissionSequence>=1
                      AND (m.RemovalSequence IS NULL OR (typeof(m.RemovalSequence)='integer' AND m.RemovalSequence>m.AdmissionSequence))
                      AND (v.ContentId IS NULL OR (typeof(v.ContentId)='text' AND length(CAST(v.ContentId AS BLOB))=36))
                      AND (v.ContentRevision IS NULL OR (typeof(v.ContentRevision)='integer' AND v.ContentRevision>=1))
                      AND (v.ByteLength IS NULL OR (typeof(v.ByteLength)='integer' AND v.ByteLength BETWEEN 0 AND 65536))
                      AND (v.ResultId IS NULL OR (typeof(v.ResultId)='text' AND length(CAST(v.ResultId AS BLOB))=36))
                      AND (q.SessionId IS NULL OR (typeof(q.SessionId)='text' AND length(CAST(q.SessionId AS BLOB))=36))
                      AND (q.CampaignId IS NULL OR (typeof(q.CampaignId)='text' AND length(CAST(q.CampaignId AS BLOB))=36))
                      AND (c.ContentId IS NULL OR (c.SessionId IS q.SessionId AND c.CampaignId IS q.CampaignId
                        AND typeof(c.ContentKindCode)='integer' AND typeof(c.Revision)='integer'
                        AND typeof(c.SensitivityCode)='integer' AND typeof(c.ProvenanceModeCode)='integer'
                        AND c.ExactGenerationIds IS NULL AND c.GenerationBloom IS NULL))
                      AND (c.SourceRecordId IS NULL OR length(CAST(c.SourceRecordId AS BLOB))=36)
                      AND (c.MimeType IS NULL OR length(CAST(c.MimeType AS BLOB))<=32)
                      AND (c.Digest IS NULL OR (typeof(c.Digest)='blob' AND length(c.Digest)=32))
                      AND (v.AgreementDigest IS NULL OR (typeof(v.AgreementDigest)='blob' AND length(v.AgreementDigest)=32))
                      THEN 1 ELSE 0 END,
                    CASE WHEN m.AdmissionSequence<=$closed AND (m.RemovalSequence IS NULL OR m.RemovalSequence>$closed) THEN 1 ELSE 0 END,
                    q.Id
                FROM guild_cohort_contributions v
                JOIN guild_cohort_members m ON m.Id=v.MemberId AND m.CohortId=v.CohortId AND m.PrimaryVotingQuestId=v.PrimaryVotingQuestId
                JOIN guild_quests q ON q.Id=v.PrimaryVotingQuestId AND q.AgentId=m.AgentId AND q.RootQuestId=$root
                LEFT JOIN guild_content c ON c.ContentId=v.ContentId
                WHERE v.CohortId=$cohort AND v.ResultSequence>$after AND v.ResultSequence<=$snapshot
                ORDER BY v.ResultSequence LIMIT 1
                """);

            Add(command, "$cohort", Id(cohort.Id));
            Add(command, "$root", Id(cohort.RootQuestId));
            Add(command, "$after", after);
            Add(command, "$snapshot", snapshot);
            Add(command, "$closed", cohort.ClosedMembershipVersion);
            Add(command, "$contentKind", (int)GuildContentKind.CohortContribution);
            Add(command, "$mime", cohort.ResultContract.MimeType);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<EvaluationCandidate?>.Success(null);

            if (reader.GetInt64(21) != 1) return Unavailable;

            long sequence = reader.GetInt64(0);

            Guid member = Guid.Parse(reader.GetString(1));

            GuildContributionOutcome outcome = (GuildContributionOutcome)reader.GetInt32(2);

            if (!reader.GetBoolean(22))
                return Result<EvaluationCandidate?>.Success(new(sequence, member, outcome, null, null, null, false));

            if (outcome != GuildContributionOutcome.Accepted)
                return Result<EvaluationCandidate?>.Success(new(sequence, member, outcome, null, null, null));

            if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(6) || reader.IsDBNull(7)) return Unavailable;

            Guid content = Guid.Parse(reader.GetString(3));

            Guid? session = reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8));

            Guid? campaign = reader.IsDBNull(9) ? null : Guid.Parse(reader.GetString(9));

            if (campaign != cohort.CampaignId) return Unavailable;

            bool erased = reader.GetBoolean(20);

            if (reader.IsDBNull(10))
                return erased ? Result<EvaluationCandidate?>.Success(new(sequence, member, GuildContributionOutcome.Invalid, null, null, null)) : Unavailable;

            if (erased || reader.GetBoolean(19)
                || reader.GetInt32(10) != (int)GuildContentKind.CohortContribution
                || reader.GetString(11) != reader.GetString(7) || reader.GetInt64(12) != reader.GetInt64(4)
                || reader.GetString(14) != cohort.ResultContract.MimeType
                || reader.GetInt64(15) != reader.GetInt64(6) || reader.GetInt64(16) != 0 || reader.GetInt64(17) != 0) return Unavailable;

            Guid sourceQuest = Guid.Parse(reader.GetString(23));

            Guid result = Guid.Parse(reader.GetString(7));

            long contentRevision = reader.GetInt64(4);

            long byteLength = reader.GetInt64(6);

            await reader.DisposeAsync().ConfigureAwait(false);

            Result owner = await GuildStore.RequireOwnerWithinAsync(connection, transaction, session, campaign, ct).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            Result<GuildResultSourceState> source = await ReadCurrentResultSourceStateWithinAsync(connection, transaction, sourceQuest, result, ct).ConfigureAwait(false);

            if (source.IsFailure) return source.Error;

            if (source.Value == GuildResultSourceState.Erased)
                return Result<EvaluationCandidate?>.Success(new(sequence, member, GuildContributionOutcome.Invalid, null, null, null));

            if (source.Value != GuildResultSourceState.Clean) return Unavailable;

            await using SqliteCommand body = Command(connection, transaction, """
                SELECT CASE WHEN typeof(c.Body)='blob' AND length(c.Body)<=65536 THEN c.Body END,
                    CASE WHEN typeof(c.Digest)='blob' AND length(c.Digest)=32 THEN c.Digest END,
                    CASE WHEN typeof(v.AgreementDigest)='blob' AND length(v.AgreementDigest)=32 THEN v.AgreementDigest END
                FROM guild_content c JOIN guild_cohort_contributions v ON v.ContentId=c.ContentId
                WHERE v.CohortId=$cohort AND v.MemberId=$member AND v.ResultSequence=$sequence
                    AND c.ContentId=$content AND c.Revision=$revision AND length(c.Body)=$bytes
                    AND c.SensitivityCode=0 AND c.ProvenanceModeCode=0 AND c.ExactGenerationIds IS NULL AND c.GenerationBloom IS NULL
                    AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity l WHERE l.ArtifactKindCode=17
                        AND lower(replace(l.ArtifactId,'-',''))=lower(replace(c.ContentId,'-','')))
                """);

            Add(body, "$cohort", Id(cohort.Id));
            Add(body, "$member", Id(member));
            Add(body, "$sequence", sequence);
            Add(body, "$content", Id(content));
            Add(body, "$revision", contentRevision);
            Add(body, "$bytes", byteLength);

            await using SqliteDataReader clean = await body.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await clean.ReadAsync(ct).ConfigureAwait(false) || clean.GetValue(0) is not byte[] bytes
                || clean.GetValue(1) is not byte[] digest || clean.GetValue(2) is not byte[] agreement
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), digest)) return Unavailable;

            Result<string> valid = GuildCohortResultPolicy.ValidateResult(cohort.ResultContract, cohort.ResultContract.MimeType, bytes);

            if (valid.IsFailure || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(valid.Value), agreement)) return Unavailable;

            return Result<EvaluationCandidate?>.Success(new(sequence, member, outcome, content, bytes, agreement));
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            return Unavailable;
        }
    }

    private static async Task<Result<List<EvaluationBucket>>> ReadEvaluationBucketsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid cohort, long generation, string mime, byte[] agreement, long after, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 10) return Unavailable;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT b.BucketId,b.VoteCount,CASE WHEN typeof(c.Body)='blob' AND length(c.Body)<=65536 THEN c.Body END,
                CASE WHEN typeof(b.BucketId)='integer' AND typeof(b.VoteCount)='integer' THEN 1 ELSE 0 END
            FROM guild_cohort_eval_buckets b LEFT JOIN guild_content c ON c.ContentId=b.RepresentativeContentId
            WHERE b.CohortId=$cohort AND b.Generation=$generation AND b.AgreementDigest=$digest AND b.BucketId>$after
            ORDER BY b.BucketId LIMIT $limit
            """);

        Add(command, "$cohort", Id(cohort));
        Add(command, "$generation", generation);
        Add(command, "$digest", agreement);
        Add(command, "$after", after);
        Add(command, "$limit", limit);

        List<EvaluationBucket> buckets = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.GetInt64(3) != 1 || reader.GetInt64(0) < 1 || reader.GetInt64(1) < 1 || reader.GetValue(2) is not byte[] body) return Unavailable;

            Result<string> valid = GuildCohortResultPolicy.ValidateResult(new(mime), mime, body);

            if (valid.IsFailure || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(valid.Value), agreement)) return Unavailable;

            buckets.Add(new(reader.GetInt64(0), body, reader.GetInt64(1)));
        }

        return buckets;
    }

    private static async Task<Result<bool>> CommitEvaluationDecisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        CohortDto cohort, long generation, GuildConvergenceDecision decision, long? bucket, long? through,
        IGuildQuestHostPolicy policy, EvaluationClock expectedClock, DateTimeOffset now, CancellationToken ct)
    {
        Result<EvaluationClock> currentClock = await ReadEvaluationClockAsync(connection, transaction, cohort.Id, policy, ct).ConfigureAwait(false);

        if (currentClock.IsFailure || currentClock.Value != expectedClock) return Unavailable;

        if (cohort.Revision == long.MaxValue) return Unavailable;

        if (decision == GuildConvergenceDecision.Success)
        {
            await using SqliteCommand selection = Command(connection, transaction, """
                INSERT INTO guild_cohort_selections(CohortId,Generation,KindCode,WinningBucket,ThroughSequence)
                VALUES($cohort,$generation,$kind,$bucket,$through)
                """);

            Add(selection, "$cohort", Id(cohort.Id));
            Add(selection, "$generation", generation);
            Add(selection, "$kind", (int)cohort.Policy.Kind);
            Add(selection, "$bucket", bucket);
            Add(selection, "$through", through);

            if (await selection.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Unavailable;
        }

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, cohort.RootQuestId,
            cohort.IssuerQuestId, GuildEventKind.CohortChanged, cohort.Id, cohort.Revision + 1, null, now, ct).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        await using SqliteCommand commit = Command(connection, transaction, """
            UPDATE guild_cohorts SET StateCode=3,DecisionCode=$decision,Revision=Revision+1,EventWatermark=$watermark
            WHERE Id=$cohort AND StateCode=2 AND Revision=$revision AND DecisionCode IS NULL;
            INSERT INTO guild_cohort_stop_frontiers(Id,CohortId,MemberId,LastMemberSequence,LastAssociationSequence,Completed)
            VALUES($frontier,$cohort,NULL,0,0,0);
            """);

        Add(commit, "$cohort", Id(cohort.Id));
        Add(commit, "$decision", (int)decision);
        Add(commit, "$watermark", projected.Value);
        Add(commit, "$revision", cohort.Revision);
        Add(commit, "$frontier", Id(Guid.NewGuid()));

        return await commit.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 2 ? true : Unavailable;
    }
}
