using System.Data.Common;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SagaMemoryStore
{
    public Task<RetroDownfall.Arcanum.Core.Primitives.Result<SagaMemoryCurationRow[]>> SearchMemoryPageAsync(
        string query, Guid? sessionId, MemoryScope scope, int limit, SagaMemoryPosition? after, CancellationToken cancellationToken, int contentByteLimit = 65536) =>
        GuildMemoryAccess.RunResultAsync(RetroDownfall.Arcanum.Core.Guild.GuildMemoryStore.Saga,
            RetroDownfall.Arcanum.Core.Guild.GuildMemoryOperation.Read, async ct =>
        {
            SagaMemoryCurationRow[] rows = await ReadLongRestSnapshotAsync(async (connection, longRest) =>
            {
                SagaRankingReader ranking = await SagaRankingReader.CreateAsync(connection, options.CurrentValue.Features.LongRest,
                    (timeProvider ?? TimeProvider.System).GetUtcNow(), ct).ConfigureAwait(false);

                await using DbCommand command = connection.CreateCommand();

                BuildListCommand(command, query, sessionId, scope, limit, 0, includeEmbeddingProbe: true, longRest,
                    cleanRecall: true, after: after);

                command.CommandText = command.CommandText.Replace("THEN m.\"Content\" ELSE NULL END AS \"Content\"",
                    "THEN CASE WHEN octet_length(m.Content)>@searchContentBytes THEN substr(m.Content,1,@searchContentChars)||@searchExcerptMarker ELSE m.Content END ELSE NULL END AS \"Content\"", StringComparison.Ordinal);

                AddParameter(command, "@searchContentBytes", contentByteLimit);

                AddParameter(command, "@searchContentChars", Math.Max(1, (contentByteLimit - 96) / 4));

                AddParameter(command, "@searchExcerptMarker", " [TRUNCATED excerpt; inspect the source for full content]");

                List<SagaMemoryCurationRow> page = [];

                await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (reader.IsDBNull(1)) throw new InvalidOperationException("A model search payload exceeds its supported allocation.");

                    SagaMemoryDto memory = ReadMemory(reader, ranking);

                    page.Add(new(memory, new(memory.RetiredAtUtc, memory.PinnedAtUtc), reader.GetInt32(18) == 1,
                        new(reader.GetString(2), reader.GetString(0))));
                }

                return page.ToArray();
            }, ct).ConfigureAwait(false);

            return RetroDownfall.Arcanum.Core.Primitives.Result<SagaMemoryCurationRow[]>.Success(rows);
        }, cancellationToken);

    public Task<SagaMemoryCurationRow[]> ListCleanRecallRowsAsync(string query, MemoryScope scope,
        int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512 || limit is < 1 or > MemoryRecallPager.MaximumCandidatesPerSource)
        {
            return Task.FromException<SagaMemoryCurationRow[]>(new ArgumentException("The clean recall window is invalid."));
        }

        return ReadLongRestSnapshotAsync(async (connection, longRest) =>
        {
            SagaRankingReader ranking = await SagaRankingReader.CreateAsync(connection, options.CurrentValue.Features.LongRest,
                (timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken).ConfigureAwait(false);

            await using DbCommand command = connection.CreateCommand();

            BuildListCommand(command, query, null, scope, limit, 0, includeEmbeddingProbe: true, longRest, cleanRecall: true);

            List<SagaMemoryCurationRow> rows = new(limit);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1))
                {
                    throw new InvalidOperationException("A clean recall payload exceeds its supported bounds.");
                }

                SagaMemoryDto memory = ReadMemory(reader, ranking);

                rows.Add(new(memory, new(memory.RetiredAtUtc, memory.PinnedAtUtc), reader.GetInt32(18) == 1));
            }

            return rows.ToArray();
        }, cancellationToken);
    }
}

/// <summary>Existing clean recall source eligibility, shared before Guild ranking and final hydration.</summary>
internal static class SagaCleanRecallSql
{
    internal static string GuildEligible(string memoryAlias) => GuildMemoryAccess.IsGuildInvocation
        ? Eligible(memoryAlias)
        : "1=1";

    internal static string Eligible(string memoryAlias) =>
        $"""
        {memoryAlias}."RetiredAtUtc" IS NULL
        AND EXISTS(SELECT 1 FROM saga_memory_embeddings clean_embedding WHERE clean_embedding.MemoryId = {memoryAlias}."Id")
        AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity clean_label WHERE clean_label.ArtifactKindCode = 6
                     AND lower(replace(clean_label.ArtifactId, '-', '')) = lower(replace({memoryAlias}."Id", '-', '')))
                 AND NOT EXISTS(
                     SELECT 1 FROM annal_claims clean_claim
                     LEFT JOIN annal_heads clean_head ON clean_head.ClaimId = clean_claim.ClaimId AND clean_head.SubjectStoreCode = 1
                     LEFT JOIN annal_versions clean_version ON clean_version.VersionId = clean_head.CurrentVersionId AND clean_version.ClaimId = clean_claim.ClaimId
                     WHERE clean_claim.SubjectStoreCode = 1
                         AND lower(replace(clean_claim.SubjectId, '-', '')) = lower(replace({memoryAlias}."Id", '-', ''))
                         AND (clean_head.CurrentVersionId IS NULL OR clean_version.VersionId IS NULL OR clean_version.SensitivityCode <> 0
                             OR clean_head.CurrentOperationCode = 3 OR clean_version.OperationCode = 3
                             OR clean_version.ScopeKindCode <> {memoryAlias}.ScopeKindCode
                             OR COALESCE(lower(replace(clean_version.CampaignId, '-', '')), '') <> COALESCE(lower(replace({memoryAlias}.CampaignId, '-', '')), '')
                             OR EXISTS(SELECT 1 FROM owner_deletion_events clean_source_deleted
                                 WHERE clean_source_deleted.OwnerKindCode = 2
                                     AND lower(replace(clean_source_deleted.OwnerId, '-', '')) = lower(replace(clean_version.SourceSessionId, '-', '')))))
                 AND ({memoryAlias}.CampaignId IS NULL OR EXISTS(SELECT 1 FROM "Campaigns" clean_campaign WHERE clean_campaign."Id" = {memoryAlias}.CampaignId))
                 AND ({memoryAlias}.SessionId IS NULL OR EXISTS(SELECT 1 FROM "Sessions" clean_session WHERE lower(replace(clean_session."Id", '-', '')) = lower(replace({memoryAlias}.SessionId, '-', ''))))
                 AND NOT EXISTS(SELECT 1 FROM owner_deletion_events clean_deleted
                     WHERE (clean_deleted.OwnerKindCode = 1 AND lower(replace(clean_deleted.OwnerId, '-', '')) = lower(replace({memoryAlias}.CampaignId, '-', '')))
                         OR (clean_deleted.OwnerKindCode = 2 AND lower(replace(clean_deleted.OwnerId, '-', '')) = lower(replace({memoryAlias}.SessionId, '-', '')))
                         OR (clean_deleted.OwnerKindCode = 1 AND EXISTS(SELECT 1 FROM "Sessions" clean_source_session
                             WHERE lower(replace(clean_source_session."Id", '-', '')) = lower(replace({memoryAlias}.SessionId, '-', '')) AND clean_source_session.CampaignId = clean_deleted.OwnerId)))
        """;
}
