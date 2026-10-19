using System.Data.Common;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>One clock instant and bounded evidence cut, shared by selection and inspection.</summary>
internal sealed class SagaRankingReader(
    bool enabled,
    bool hasAnnals,
    DateTimeOffset asOfUtc,
    IReadOnlyDictionary<string, SagaRankingEvidence> evidence)
{
    internal bool Enabled => enabled;

    internal DateTimeOffset AsOfUtc => asOfUtc;

    internal static async Task<SagaRankingReader> CreateAsync(DbConnection connection, bool enabled, DateTimeOffset asOfUtc, CancellationToken ct)
    {
        int version = await GrimoireCoreSchemaVersion.ReadAsync(connection, ct).ConfigureAwait(false);
        bool active = enabled && version >= 20;
        IReadOnlyDictionary<string, SagaRankingEvidence> evidence = active
            ? await SagaRankingEvidenceReader.ReadAsync(connection, asOfUtc, ct).ConfigureAwait(false)
            : new Dictionary<string, SagaRankingEvidence>(0);
        return new(active, version >= 17, asOfUtc, evidence);
    }

    internal static string Join(bool available) => available ? """
        LEFT JOIN annal_claims ranking_claim ON ranking_claim.SubjectStoreCode = 1 AND ranking_claim.SubjectId = m."Id"
        LEFT JOIN annal_heads ranking_head ON ranking_head.ClaimId = ranking_claim.ClaimId AND ranking_head.SubjectStoreCode = 1
        LEFT JOIN annal_versions ranking_version ON ranking_version.VersionId = ranking_head.CurrentVersionId
            AND ranking_version.ClaimId = ranking_claim.ClaimId
        """ : "";

    internal static string Projection(bool available) => available ? """
        , m."Id" AS RankingMemoryId, m."CreatedAt" AS RankingCreatedAt,
          m."RetiredAtUtc" AS RankingRetiredAt, m."PinnedAtUtc" AS RankingPinnedAt,
          ranking_version.VersionId AS RankingVersionId, ranking_version.ClaimId AS RankingClaimId,
          ranking_version.OriginCode AS RankingOrigin, ranking_version.RecordedAtUtc AS RankingRecordedAt,
          ranking_version.ValidFromUtc AS RankingValidFrom, ranking_version.ValidToUtc AS RankingValidTo,
          ranking_version.ContentHashFormatCode AS RankingHashFormat, ranking_version.ContentHash AS RankingContentHash,
          ranking_version.OperationCode AS RankingOperation,
          ranking_version.ScopeKindCode AS RankingScopeKind, ranking_version.CampaignId AS RankingCampaignId,
          m.ScopeKindCode AS RankingMemoryScopeKind, m.CampaignId AS RankingMemoryCampaignId,
          CASE WHEN ranking_version.SensitivityCode <> 0 OR EXISTS (
              SELECT 1 FROM artifact_sensitivity ranking_label WHERE ranking_label.ArtifactKindCode = 6
                  AND lower(replace(ranking_label.ArtifactId, '-', '')) = lower(replace(m."Id", '-', ''))
          ) THEN 1 ELSE 0 END AS RankingProtected
        """ : "";

    internal SagaRankingSnapshot Snapshot(DbDataReader reader, bool consolidated = false)
    {
        if (!hasAnnals)
        {
            return new(null, null, DateTimeOffset.MinValue, null, null, false, false, consolidated, new());
        }

        string? version = Text(reader, "RankingVersionId");
        SagaRankingEvidence proof = version is not null && evidence.TryGetValue(version, out SagaRankingEvidence? found) ? found : new();
        return new(version, Number(reader, "RankingOrigin") is { } origin ? (AnnalOrigin)origin : null,
            Instant(reader, "RankingRecordedAt") ?? Instant(reader, "RankingCreatedAt")!.Value,
            Instant(reader, "RankingValidFrom"), Instant(reader, "RankingValidTo"),
            Text(reader, "RankingPinnedAt") is not null,
            Text(reader, "RankingRetiredAt") is not null || Number(reader, "RankingOperation") == 3,
            consolidated, proof);
    }

    internal SagaRankingComponents Evaluate(DbDataReader reader, float similarity, bool consolidated = false) =>
        SagaRankingPolicy.Evaluate(similarity, Snapshot(reader, consolidated), asOfUtc, enabled);

    internal bool Eligible(DbDataReader reader, SagaRankingComponents components) =>
        !enabled || (Text(reader, "RankingRetiredAt") is null && Number(reader, "RankingOperation") != 3
            && !components.Dormant && components.TemporallyValid);

    internal SagaMemoryDto Enrich(SagaMemoryDto memory, DbDataReader reader)
    {
        if (!enabled)
        {
            return memory;
        }

        SagaUseCoordinate? coordinate = null;
        if (hasAnnals && Text(reader, "RankingVersionId") is { } version && Text(reader, "RankingClaimId") is { } claim
            && Number(reader, "RankingHashFormat") == 1 && Number(reader, "RankingProtected") == 0
            && Number(reader, "RankingOperation") != 3
            && Number(reader, "RankingScopeKind") == Number(reader, "RankingMemoryScopeKind")
            && SameCampaign(Text(reader, "RankingCampaignId"), Text(reader, "RankingMemoryCampaignId"))
            && reader[reader.GetOrdinal("RankingContentHash")] is byte[] hash
            && hash.AsSpan().SequenceEqual(AnnalContentDigest.ForSagaMemory(memory.Content)))
        {
            coordinate = new(memory.Id, claim, version, 1, new CovenantDigest(hash));
        }
        SagaRankingComponents? components = enabled ? Evaluate(reader, 0, memory.LongRestReceiptId is not null) with { VersionId = null } : null;
        return memory with { UseCoordinate = coordinate, Ranking = components };
    }

    internal static bool IsProtected(DbDataReader reader) => Number(reader, "RankingProtected") != 0;

    private static bool SameCampaign(string? left, string? right) => left is null ? right is null
        : Guid.TryParse(left, out Guid l) && Guid.TryParse(right, out Guid r) && l == r;

    private static string? Text(DbDataReader reader, string name)
    {
        int index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static int? Number(DbDataReader reader, string name)
    {
        int index = reader.GetOrdinal(name);
        return reader.IsDBNull(index) ? null : reader.GetInt32(index);
    }

    private static DateTimeOffset? Instant(DbDataReader reader, string name) =>
        Text(reader, name) is { } value ? UtcInstantText.Parse(value) : null;
}
