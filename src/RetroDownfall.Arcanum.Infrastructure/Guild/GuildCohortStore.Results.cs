using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildCohortStore
{
    internal static async Task<Result> CapturePrimaryResultWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored quest, GuildQuestResult result, DateTimeOffset now, CancellationToken ct)
    {
        Guid cohortId;
        Guid memberId;
        string mime;
        GuildCohortState state;
        GuildCohortMemberState memberState;
        long revision;
        Guid issuerQuestId;

        await using (SqliteCommand locate = Command(connection, transaction, """
            SELECT c.Id,m.Id,c.MimeType,c.StateCode,m.StateCode,c.Revision,c.IssuerQuestId FROM guild_cohort_member_quests a
            JOIN guild_cohort_members m ON m.Id=a.MemberId JOIN guild_cohorts c ON c.Id=m.CohortId
            WHERE a.QuestId=$quest AND a.AssociationKindCode=1 AND m.PrimaryVotingQuestId=$quest
            """))
        {
            Add(locate, "$quest", Id(quest.Quest.Id));

            await using SqliteDataReader reader = await locate.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result.Success();

            cohortId = Guid.Parse(reader.GetString(0));
            memberId = Guid.Parse(reader.GetString(1));
            mime = reader.GetString(2);
            state = (GuildCohortState)reader.GetInt32(3);
            memberState = (GuildCohortMemberState)reader.GetInt32(4);
            revision = reader.GetInt64(5);
            issuerQuestId = Guid.Parse(reader.GetString(6));

            if (await reader.ReadAsync(ct).ConfigureAwait(false) || !Enum.IsDefined(state) || !Enum.IsDefined(memberState)
                || mime is not ("text/plain" or "application/json")) return Unavailable;
        }

        byte[] proof = ProofDigest(result);

        await using (SqliteCommand observation = Command(connection, transaction, """
            SELECT ResultId,ProofDigest FROM guild_cohort_result_observations WHERE QuestId=$quest
            """))
        {
            Add(observation, "$quest", Id(quest.Quest.Id));

            await using SqliteDataReader reader = await observation.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != Id(result.Receipt.ResultId) || reader.GetValue(1) is not byte[] priorProof
                    || !CryptographicOperations.FixedTimeEquals(priorProof, proof))
                    return new Error(ErrorCodes.Guild.ResultConflict, "The primary result observation has changed.");
            }
            else
            {
                await reader.DisposeAsync().ConfigureAwait(false);

                await using SqliteCommand record = Command(connection, transaction, """
                    INSERT INTO guild_cohort_result_observations(QuestId,ResultId,ProofDigest) VALUES($quest,$result,$proof)
                    """);

                Add(record, "$quest", Id(quest.Quest.Id));
                Add(record, "$result", Id(result.Receipt.ResultId));
                Add(record, "$proof", proof);

                _ = await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await using (SqliteCommand prior = Command(connection, transaction, """
            SELECT ResultId,ProofDigest,ContentId FROM guild_cohort_contributions WHERE CohortId=$cohort AND MemberId=$member
            """))
        {
            Add(prior, "$cohort", Id(cohortId));
            Add(prior, "$member", Id(memberId));

            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0)) return Result.Success(); // A withdrawn vote is immutable; late output only settles the Quest.

                if (reader.GetString(0) != Id(result.Receipt.ResultId) || reader.IsDBNull(1)
                    || reader.GetValue(1) is not byte[] previous || !CryptographicOperations.FixedTimeEquals(previous, proof))
                    return new Error(ErrorCodes.Guild.ResultConflict, "The primary result was already recorded with different proof.");

                Guid? contentId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2));

                await reader.DisposeAsync().ConfigureAwait(false);

                if (contentId is { } retained && result.DesignatedResultUtf8 is { } bytes)
                {
                    await using SqliteCommand body = Command(connection, transaction, "SELECT Body FROM guild_content WHERE ContentId=$id");
                    Add(body, "$id", Id(retained));

                    object? stored = await body.ExecuteScalarAsync(ct).ConfigureAwait(false);

                    // Protected erasure keeps the immutable proof digest; it must not resurrect bytes
                    // or turn an otherwise identical settlement replay into new execution.
                    if (stored is byte[] saved && !saved.AsSpan().SequenceEqual(bytes))
                        return new Error(ErrorCodes.Guild.ResultConflict, "The primary result was already recorded with different bytes.");
                }

                return Result.Success();
            }
        }

        if (state is GuildCohortState.Draining or GuildCohortState.Completed or GuildCohortState.Failed or GuildCohortState.Canceled)
            return Result.Success();

        GuildContributionOutcome outcome = result.Receipt.Outcome switch
        {
            QuestState.Failed => GuildContributionOutcome.Failed,
            QuestState.Canceled => GuildContributionOutcome.Canceled,
            QuestState.Rejected => GuildContributionOutcome.Rejected,
            QuestState.Completed => GuildContributionOutcome.Invalid,
            _ => 0,
        };

        if (outcome == 0) return Unavailable;

        if (memberState == GuildCohortMemberState.Withdrawn) outcome = GuildContributionOutcome.Withdrawn;

        byte[]? bytesToStore = result.DesignatedResultUtf8;

        string? agreement = null;

        if (outcome == GuildContributionOutcome.Invalid && result.Receipt.Content is not null
            && result.DesignatedResultCount == 1 && bytesToStore is { Length: <= 65_536 }
            && (result.DesignatedResultMimeType is "text/plain" or "application/json")
            && (mime == "application/json" || result.DesignatedResultMimeType == "text/plain"))
        {
            Result<string> valid = GuildCohortResultPolicy.ValidateResult(new(mime), mime, bytesToStore, 1);

            if (valid.IsSuccess)
            {
                outcome = GuildContributionOutcome.Accepted;
                agreement = valid.Value;
            }
        }

        GuildDataRef? content = null;

        if (outcome == GuildContributionOutcome.Accepted)
        {
            Guid contentId = Guid.NewGuid();

            Result<DerivedArtifactWrite> label = await GuildQuestStore.ResultSensitivityWithinAsync(connection, transaction,
                quest, contentId, bytesToStore!, result.SourceLabel, ct).ConfigureAwait(false);

            if (label.IsFailure) return label.Error;

            Result<GuildDataRef> written = await GuildStore.WriteContentWithinAsync(connection, transaction,
                new(contentId, result.Receipt.ResultId, GuildContentKind.CohortContribution, 1,
                    quest.Quest.SessionId, quest.Quest.CampaignId, mime, bytesToStore!, label.Value), now, ct).ConfigureAwait(false);

            if (written.IsFailure) return written.Error;

            content = written.Value;
        }

        if (revision == long.MaxValue) return Unavailable;

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction,
            quest.Quest.RootQuestId, issuerQuestId, GuildEventKind.ContributionAccepted, cohortId,
            revision + 1, null, now, ct).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        long sequence;

        await using (SqliteCommand update = Command(connection, transaction, """
            UPDATE guild_cohorts SET ResultSequence=ResultSequence+1,EventWatermark=$watermark,Revision=Revision+1
            WHERE Id=$cohort AND StateCode IN(1,2) AND ResultSequence<9223372036854775807 AND Revision<9223372036854775807
            RETURNING ResultSequence
            """))
        {
            Add(update, "$cohort", Id(cohortId));
            Add(update, "$watermark", projected.Value);

            object? value = await update.ExecuteScalarAsync(ct).ConfigureAwait(false);

            if (value is not long next) return Unavailable;

            sequence = next;
        }

        await using SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_cohort_contributions(CohortId,MemberId,PrimaryVotingQuestId,ResultSequence,ResultId,OutcomeCode,
                ContentId,ContentRevision,ProofDigest,AgreementDigest,ByteLength,Selected)
            VALUES($cohort,$member,$quest,$sequence,$result,$outcome,$content,$revision,$proof,$agreement,$length,0)
            """);

        Add(insert, "$cohort", Id(cohortId));
        Add(insert, "$member", Id(memberId));
        Add(insert, "$quest", Id(quest.Quest.Id));
        Add(insert, "$sequence", sequence);
        Add(insert, "$result", Id(result.Receipt.ResultId));
        Add(insert, "$outcome", (int)outcome);
        Add(insert, "$content", content is null ? null : Id(content.ContentId));
        Add(insert, "$revision", content?.Revision);
        Add(insert, "$proof", proof);
        Add(insert, "$agreement", agreement is null ? null : Convert.FromHexString(agreement));
        Add(insert, "$length", content?.ByteLength);

        _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return Result.Success();
    }

    private static byte[] ProofDigest(GuildQuestResult result)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData("guild-primary-proof-v1\n"u8);
        Span<byte> length = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(length, result.DesignatedResultCount is { } count ? count : -1);
        hash.AppendData(length);

        byte[]? mime = result.DesignatedResultMimeType is null ? null : Encoding.UTF8.GetBytes(result.DesignatedResultMimeType);
        BinaryPrimitives.WriteInt64BigEndian(length, mime?.Length ?? -1);
        hash.AppendData(length);
        if (mime is not null) hash.AppendData(mime);

        BinaryPrimitives.WriteInt64BigEndian(length, result.DesignatedResultUtf8?.Length ?? -1);
        hash.AppendData(length);
        if (result.DesignatedResultUtf8 is not null) hash.AppendData(result.DesignatedResultUtf8);

        return hash.GetHashAndReset();
    }
}
