using System.Data.Common;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    private const string InspectionJoins = """
        LEFT JOIN artifact_sensitivity a ON a.ArtifactKindCode = 7
            AND a.ArtifactId = upper(substr(e.Id,1,8)||'-'||substr(e.Id,9,4)||'-'||substr(e.Id,13,4)||'-'||substr(e.Id,17,4)||'-'||substr(e.Id,21,12))
        LEFT JOIN annal_claims c ON c.SubjectStoreCode = 2 AND c.SubjectId = e.Id
        LEFT JOIN annal_heads h ON h.ClaimId = c.ClaimId
        LEFT JOIN annal_versions v ON v.VersionId = h.CurrentVersionId AND v.ClaimId = c.ClaimId
        """;

    private static string InspectionEvidenceColumns(bool curation) => $"""
        e.Id, e.ScopeCampaignId, {(curation ? "e.RetiredAtUtc" : "NULL")} AS RetiredAtUtc,
        a.LabelId, a.ArtifactKindCode, a.ArtifactId, a.SessionId, a.CampaignId, a.TurnId,
        a.ArtifactRevision, a.ArtifactContentDigest, a.SensitivityCode, a.ProvenanceModeCode,
        a.ExactGenerationIds, a.GenerationBloom, a.SensitivityDigest, a.ProducingPlanDigest,
        a.ProducingAdmissionDigest, a.ProducingMaintenanceReceiptDigest, a.ArtifactLabelDigest, a.CreatedAtUtc,
        c.ClaimId, h.CurrentVersionId, h.CurrentRevision, h.CurrentOperationCode, h.SubjectStoreCode,
        v.VersionId, v.ClaimId, v.Sequence, v.Revision, v.OperationCode, v.OriginCode,
        v.ScopeKindCode, v.CampaignId, v.SensitivityCode, {(curation ? "v.ContentHashFormatCode" : "1")} AS ContentHashFormatCode, v.ContentHash,
        v.ValidFromUtc, v.ValidToUtc, v.RecordedAtUtc, NULL, v.PredecessorVersionId,
        EXISTS(SELECT 1 FROM annal_versions later WHERE later.ClaimId = c.ClaimId AND later.Revision > v.Revision) AS HasLaterVersion,
        count(*) OVER (PARTITION BY e.Id) AS EvidenceCopies,
        {(curation ? "e.PinnedAtUtc" : "NULL")} AS PinnedAtUtc, {(curation ? "e.CurationGeneration" : "1")} AS CurationGeneration
        """;

    private async Task<InspectionCounts> VerifyInspectionAuthorityAsync(
        DbConnection connection, bool curation, ICovenantSnapshotReadLease? lease,
        Guid? selectedId, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        // An orphan label cannot silently turn a formerly protected row into an unlabeled row.
        // Other artifact kinds may legitimately reuse a GUID and are not Lexicon evidence.
        command.CommandText = """
            SELECT count(*) AS OrphanLabels FROM artifact_sensitivity a WHERE a.ArtifactKindCode = 7
                AND NOT EXISTS(SELECT 1 FROM lexicon_entries e WHERE
                    e.Id = lower(replace(a.ArtifactId, '-', ''))
                    AND a.ArtifactId = upper(substr(e.Id,1,8)||'-'||substr(e.Id,9,4)||'-'||substr(e.Id,13,4)||'-'||substr(e.Id,17,4)||'-'||substr(e.Id,21,12)))
            """;

        RequireIntegrity(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0);

        command.CommandText = $"SELECT {InspectionEvidenceColumns(curation)} FROM lexicon_entries e {InspectionJoins}"
            + (selectedId is null ? "" : " WHERE e.Id = @id");

        if (selectedId is { } id)
        {
            AddParameter(command, "@id", id.ToString("N"));
        }

        bool protectedContent = false;

        int retained = 0;

        int eligible = 0;

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            InspectionEvidence evidence = ReadInspectionEvidence(reader, 0, lease);

            RequireProtectedLease(evidence.Label, lease);

            protectedContent |= evidence.Label is not null;

            retained++;

            eligible += reader.IsDBNull(2) ? 1 : 0;
        }

        return new InspectionCounts(retained, eligible, protectedContent);
    }

    private static InspectionEvidence ReadInspectionEvidence(DbDataReader reader, int offset, ICovenantSnapshotReadLease? lease)
    {
        Guid id = Guid.Parse(reader.GetString(offset));

        RequireIntegrity(id != Guid.Empty && reader.GetInt64(offset + 43) == 1);

        LexiconCurationScope scope = ScopeFromKey(reader.GetString(offset + 1));

        bool retired = !reader.IsDBNull(offset + 2);

        if (retired)
        {
            _ = UtcInstantText.Parse(reader.GetString(offset + 2));
        }

        if (!reader.IsDBNull(offset + 44))
        {
            _ = UtcInstantText.Parse(reader.GetString(offset + 44));
        }

        _ = ReadPositiveInteger(reader, offset + 45);

        ArtifactSensitivityLabel? label = reader.IsDBNull(offset + 3) ? null
            : ReadInspectionLabel(reader, id, scope, offset + 3);

        RequireProtectedLease(label, lease);

        AnnalClaimVersion? head = null;

        if (!reader.IsDBNull(offset + 21))
        {
            RequireIntegrity(!reader.IsDBNull(offset + 22) && !reader.IsDBNull(offset + 26)
                && AnnalsStore.ReadCode(reader, offset + 25, 2, 2) == 2 && reader.GetInt64(offset + 42) == 0);

            head = ReadInspectionVersion(reader, offset + 26);

            VerifyVersionScope(head, scope);

            RequireIntegrity(head.ClaimId == reader.GetString(offset + 21)
                && head.VersionId == reader.GetString(offset + 22)
                && head.Revision == AnnalsStore.ReadCode(reader, offset + 23, 1, int.MaxValue)
                && (int)head.Operation == AnnalsStore.ReadCode(reader, offset + 24, 1, 3)
                && (head.Operation == AnnalOperation.Retire) == retired
                && head.Sensitivity == (label?.Sensitivity ?? ContentSensitivity.None));
        }
        else
        {
            RequireIntegrity(!retired);
        }

        return new InspectionEvidence(id, label, head);
    }

    private async Task<bool> StreamInspectionAsync(
        DbConnection connection, string? query, int? limit, ICovenantSnapshotReadLease? lease,
        Action<LexiconEntryDto> accept, CancellationToken cancellationToken, bool? protectedOnlyCuration = null)
    {
        bool curation = protectedOnlyCuration ?? await HasCurationAsync(connection, cancellationToken).ConfigureAwait(false);

        bool protectedContent = protectedOnlyCuration is not null
            || (await VerifyInspectionAuthorityAsync(connection, curation, lease, null, cancellationToken).ConfigureAwait(false)).Protected;

        await using DbCommand command = connection.CreateCommand();

        // Structural validation cannot be hidden by a search predicate or LIMIT. It reads facts
        // only after metadata authority succeeds; count-only callers reach only protected rows.
        command.CommandText = """
            SELECT count(*) AS InvalidFacts FROM lexicon_entries e
            WHERE (@protectedOnly = 0 OR EXISTS(SELECT 1 FROM artifact_sensitivity a WHERE a.ArtifactKindCode = 7
                AND a.ArtifactId = upper(substr(e.Id,1,8)||'-'||substr(e.Id,9,4)||'-'||substr(e.Id,13,4)||'-'||substr(e.Id,17,4)||'-'||substr(e.Id,21,12))))
                AND CASE
                    WHEN json_valid(e.FactsJson) = 0 THEN 1
                    WHEN json_type(e.FactsJson) <> 'array' THEN 1
                    WHEN json_array_length(e.FactsJson) = 0 THEN 1
                    ELSE EXISTS(SELECT 1 FROM json_each(e.FactsJson) f WHERE f.type <> 'text')
                END
            """;

        AddParameter(command, "@protectedOnly", protectedOnlyCuration is not null ? 1 : 0);

        RequireIntegrity(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 0);

        // Selection is bounded before canonical projection. The deterministic scalar retains .NET
        // OrdinalIgnoreCase, including Unicode, and json_each keeps fact boundaries significant.
        command.CommandText = $"""
            WITH selected AS MATERIALIZED (
                SELECT e.Id FROM lexicon_entries e
                WHERE (@protectedOnly = 0 OR EXISTS(SELECT 1 FROM artifact_sensitivity a WHERE a.ArtifactKindCode = 7
                    AND a.ArtifactId = upper(substr(e.Id,1,8)||'-'||substr(e.Id,9,4)||'-'||substr(e.Id,13,4)||'-'||substr(e.Id,17,4)||'-'||substr(e.Id,21,12))))
                    AND (@query IS NULL OR arcanum_ordinal_contains(e.Name, @query)
                    OR arcanum_ordinal_contains(e.Type, @query)
                    OR EXISTS(SELECT 1 FROM json_each(e.FactsJson) f WHERE arcanum_ordinal_contains(f.value, @query)))
                ORDER BY e.Name COLLATE NOCASE, e.ScopeCampaignId, e.Id LIMIT @limit
            )
            SELECT e.Id, e.Name, e.Type, e.FactsJson, e.UpdatedAt, e.ScopeCampaignId,
                   {(curation ? "e.RetiredAtUtc" : "NULL")} AS RetiredAtUtc,
                   {(curation ? "e.PinnedAtUtc" : "NULL")} AS PinnedAtUtc,
                   {(curation ? "e.CurationGeneration" : "1")} AS CurationGeneration,
                   e.NameNormalized, e.FactsText,
                   {InspectionEvidenceColumns(curation)},
                   (SELECT json_group_array(json_array(p.Fact, p.SessionId, p.AttachmentId, p.LogicalKey,
                       p.Version, p.ContentHash, p.MaterializedAt, p.SourceType,
                       EXISTS(SELECT 1 FROM SessionAttachments b WHERE b.Id = p.AttachmentId AND b.State = 'Bound')) ORDER BY p.rowid)
                    FROM lexicon_fact_attachment_provenance p WHERE p.EntryId = e.Id) AS FactProvenanceJson
            FROM selected s JOIN lexicon_entries e ON e.Id = s.Id {InspectionJoins}
            ORDER BY e.Name COLLATE NOCASE, e.ScopeCampaignId, e.Id
            """;

        AddParameter(command, "@query", string.IsNullOrWhiteSpace(query) ? DBNull.Value : query.Trim());

        AddParameter(command, "@limit", limit ?? -1);

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            InspectionEvidence evidence = ReadInspectionEvidence(reader, 11, lease);

            RequireProtectedLease(evidence.Label, lease);

            InspectionRow row = ReadInspectionRow(reader, evidence.Id);

            row = row with { Entry = row.Entry with { FactProvenance = ReadProjectedProvenance(reader.GetString(57)) } };

            VerifyCurrentProvenance(row.Entry);

            if (evidence.Label is { } label)
            {
                RequireIntegrity(label.ArtifactContentDigest == DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(row.Canonical)));
            }

            if (evidence.Head is { Operation: not AnnalOperation.Retire } head)
            {
                byte[] digest = head.ContentHashFormat == AnnalContentHashFormat.LegacyStoreDigest
                    ? AnnalContentDigest.ForLexiconEntry(row.Canonical.Type, row.Canonical.FactsText)
                    : LexiconSnapshotDigest.Compute(row.Canonical);

                RequireIntegrity(head.ContentHash!.AsSpan().SequenceEqual(digest));
            }

            accept(row.Entry);
        }

        return protectedContent;
    }

    private static LexiconFactProvenance[] ReadProjectedProvenance(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        List<LexiconFactProvenance> facts = [];

        foreach (JsonElement source in document.RootElement.EnumerateArray())
        {
            RequireIntegrity(source[4].TryGetInt32(out int version) && version > 0);

            facts.Add(new LexiconFactProvenance(source[0].GetString()!, new AttachmentMemoryProvenance(
                Guid.Parse(source[1].GetString()!), Guid.Parse(source[2].GetString()!), source[3].GetString()!,
                version, source[5].GetString()!, UtcInstantText.Parse(source[6].GetString()!),
                source[7].GetString()!, source[8].GetInt32() == 1
                    ? AttachmentSourceAvailability.Available : AttachmentSourceAvailability.Unavailable)));
        }

        return [.. facts];
    }

    private sealed record InspectionEvidence(Guid Id, ArtifactSensitivityLabel? Label, AnnalClaimVersion? Head);

    private sealed record InspectionCounts(int Retained, int Eligible, bool Protected);
}
