using System.Data.Common;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class SagaMemoryStore
{
    public Task<SagaRankingExplanationDto?> ExplainRankingAsync(MemoryScope scope, string? memoryId, float referenceSimilarity, CancellationToken cancellationToken)
    {
        if (!float.IsFinite(referenceSimilarity) || referenceSimilarity is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceSimilarity));
        }

        return ReadLongRestSnapshotAsync<SagaRankingExplanationDto?>(async (connection, longRest) =>
        {
            SagaRankingReader ranking = await SagaRankingReader.CreateAsync(connection, options.CurrentValue.Features.LongRest,
                (timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken).ConfigureAwait(false);
            List<SagaRankingExplanationMemoryDto> memories = [];
            if (longRest)
            {
                await using DbCommand command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT m."Id" {SagaRankingReader.Projection(longRest)},
                        EXISTS(SELECT 1 FROM saga_memory_embeddings embedding WHERE embedding.MemoryId = m."Id") AS RankingHasEmbedding,
                        {LongRestRecallSql.Suppressed("m")} AS RankingConsolidated
                    FROM saga_memories m
                    {SagaRankingReader.Join(longRest)}
                    WHERE (@id IS NULL OR m."Id" = @id)
                        AND (@enforced = 0 OR m.ScopeKindCode = 1 OR (m.ScopeKindCode = 2 AND m.CampaignId = @campaign))
                        AND COALESCE(ranking_version.SensitivityCode, 0) = 0
                        AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                            AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(m."Id", '-', '')))
                    ORDER BY m."CreatedAt" DESC, m."Id" COLLATE BINARY ASC LIMIT 51
                    """;
                AddParameter(command, "@id", memoryId is null ? DBNull.Value : memoryId);
                AddParameter(command, "@enforced", scope.IsEnforced ? 1 : 0);
                AddParameter(command, "@campaign", scope.CampaignId is { } campaign ? campaign.ToString("D").ToUpperInvariant() : DBNull.Value);
                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    bool consolidated = reader.GetInt32(reader.GetOrdinal("RankingConsolidated")) != 0;
                    SagaRankingComponents components = ranking.Evaluate(reader, referenceSimilarity, consolidated) with { VersionId = null };
                    SagaRetrievalEligibility eligibility = SagaRetrievalEligibilityClassifier.Classify(
                        ranking.Snapshot(reader, consolidated).Retired,
                        (SagaMemoryScopeKind)reader.GetInt32(reader.GetOrdinal("RankingMemoryScopeKind")),
                        consolidated, reader.GetInt32(reader.GetOrdinal("RankingHasEmbedding")) != 0, components, scope.IsEnforced);
                    memories.Add(new(reader.GetString(0), eligibility == SagaRetrievalEligibility.Eligible, components, eligibility));
                }
            }

            bool more = memories.Count > 50;
            if (more) memories.RemoveAt(50);
            return new SagaRankingExplanationDto(SagaRankingPolicy.PolicyVersion, ranking.Enabled, ranking.AsOfUtc, referenceSimilarity,
                -0.09f, 0.13f, memories.ToArray(), more);
        }, cancellationToken);
    }

    public Task<SagaRankingStatusDto?> ReadRankingStatusAsync(MemoryScope scope, Guid? sessionId, CancellationToken cancellationToken) =>
        ReadLongRestSnapshotAsync<SagaRankingStatusDto?>(async (connection, longRest) =>
        {
            if (!longRest)
            {
                return null;
            }

            SagaRankingReader ranking = await SagaRankingReader.CreateAsync(connection, options.CurrentValue.Features.LongRest,
                (timeProvider ?? TimeProvider.System).GetUtcNow(), cancellationToken).ConfigureAwait(false);

            await using DbCommand command = connection.CreateCommand();

            command.CommandText = $"""
                SELECT m."Id" {SagaRankingReader.Projection(longRest)},
                    {LongRestRecallSql.Suppressed("m")} AS RankingConsolidated
                FROM saga_memories m
                {SagaRankingReader.Join(longRest)}
                WHERE (@session IS NULL OR m.SessionId = @session COLLATE NOCASE)
                    AND (@enforced = 0 OR m.ScopeKindCode = 1 OR (m.ScopeKindCode = 2 AND m.CampaignId = @campaign))
                    AND COALESCE(ranking_version.SensitivityCode, 0) = 0
                    AND NOT EXISTS(SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                        AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(m."Id", '-', '')))
                """;

            AddParameter(command, "@session", sessionId is { } session ? session.ToString("D") : DBNull.Value);

            AddParameter(command, "@enforced", scope.IsEnforced ? 1 : 0);

            AddParameter(command, "@campaign", scope.CampaignId is { } campaign ? campaign.ToString("D").ToUpperInvariant() : DBNull.Value);

            int memories = 0;

            int versions = 0;

            int consolidatedVersions = 0;

            int dormantVersions = 0;

            int positiveCreditVersions = 0;

            int negativeCreditVersions = 0;

            int uncorroboratedBenefitVersions = 0;

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                memories++;

                bool consolidated = reader.GetInt32(reader.GetOrdinal("RankingConsolidated")) != 0;

                SagaRankingSnapshot snapshot = ranking.Snapshot(reader, consolidated);

                if (string.IsNullOrWhiteSpace(snapshot.VersionId))
                {
                    continue;
                }

                versions++;

                if (consolidated)
                {
                    consolidatedVersions++;
                }

                SagaRankingComponents components = ranking.Evaluate(reader, 0, consolidated);

                if (components.Dormant)
                {
                    dormantVersions++;
                }

                if (components.UsefulnessModifier > 0)
                {
                    positiveCreditVersions++;
                }
                else if (components.UsefulnessModifier < 0)
                {
                    negativeCreditVersions++;
                }

                if (components.EvidenceClass == "counterfactual-uncorroborated")
                {
                    uncorroboratedBenefitVersions++;
                }
            }

            return new(SagaRankingPolicy.PolicyVersion, ranking.Enabled, ranking.AsOfUtc, true,
                memories, versions, consolidatedVersions, dormantVersions, positiveCreditVersions,
                negativeCreditVersions, uncorroboratedBenefitVersions);
        }, cancellationToken);

    private static async Task<bool> AnyRankedRetrievableAsync(DbConnection connection, MemoryScope scope, SagaRankingReader ranking,
        bool longRest, CancellationToken ct)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m."Id" {SagaRankingReader.Projection(longRest)}
            FROM saga_memories m
            INNER JOIN saga_memory_embeddings e ON e.MemoryId = m."Id"
            {SagaRankingReader.Join(longRest)}
            WHERE m."RetiredAtUtc" IS NULL AND {LongRestRecallSql.Eligible("m", longRest)}
                AND (@enforced = 0 OR m.ScopeKindCode = 1 OR (m.ScopeKindCode = 2 AND m.CampaignId = @campaign))
            """;
        AddParameter(command, "@enforced", scope.IsEnforced ? 1 : 0);
        AddParameter(command, "@campaign", scope.CampaignId is { } campaign ? campaign.ToString("D").ToUpperInvariant() : DBNull.Value);
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var components = ranking.Evaluate(reader, 0);
            if (ranking.Eligible(reader, components)) return true;
        }

        return false;
    }
}
