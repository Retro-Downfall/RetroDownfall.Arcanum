using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

internal sealed partial class CampaignRollupStore
{
    private sealed record Segment(Guid Id, Guid LineageId, long Ordinal, Guid CampaignId,
        int FormatCode, CovenantDigest PriorDigest, CovenantDigest Digest, long Count, long Added);

    private static async Task<Segment?> ReadRootAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid artifactId, CancellationToken ct)
    {
        if (!await CampaignRollupSourceStorage.IsInstalledAsync(connection, transaction, ct).ConfigureAwait(false))
        {
            return null;
        }

        await using SqliteCommand command = Command(connection, transaction,
            """
            SELECT m.ManifestId, m.LineageId, m.Ordinal, m.CampaignId, m.HashFormatCode,
                m.PriorManifestDigest, m.SourceManifestDigest, m.SourceCount, m.AddedSourceCount
            FROM campaign_rollup_artifacts a JOIN campaign_rollup_manifests m ON m.ManifestId = a.ManifestId
            WHERE a.ArtifactId = $artifact;
            """, ("$artifact", Format(artifactId)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadSegment(reader) : null;
    }

    private static Segment ReadSegment(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)), reader.GetInt64(2), Guid.Parse(reader.GetString(3)), reader.GetInt32(4),
        new CovenantDigest((byte[])reader.GetValue(5)), new CovenantDigest((byte[])reader.GetValue(6)),
        reader.GetInt64(7), reader.GetInt64(8));

    private static Manifest SegmentHash(Guid campaignId, Guid lineage, long ordinal, CovenantDigest prior,
        long count, long added)
    {
        Manifest hash = new("Arcanum.CampaignRollup.Sources.v2\0");

        hash.Guid(campaignId);

        hash.Guid(lineage);

        hash.Number(ordinal);

        hash.Digest(prior);

        hash.Number(count);

        hash.Number(added);

        return hash;
    }

    private static async Task<Segment> AppendSharedManifestAsync(SqliteConnection connection,
        SqliteTransaction transaction, CampaignRollupInput input, CancellationToken ct)
    {
        Segment? prior = input.Previous is { } previous
            ? await ReadRootAsync(connection, transaction, previous.ArtifactId, ct).ConfigureAwait(false)
            : null;

        Guid lineage = prior?.LineageId ?? Guid.NewGuid();

        if (prior is null && input.Previous is { } legacy)
        {
            prior = new Segment(Guid.NewGuid(), lineage, 1, input.CampaignId, 1,
                new CovenantDigest(new byte[32]), legacy.SourceManifestDigest, legacy.SourceCount, legacy.SourceCount);

            await InsertSegmentAsync(connection, transaction, prior, ct).ConfigureAwait(false);

            // One legacy base import. Historical v1 rows and their digest bytes remain untouched.
            _ = await ExecuteAsync(connection, transaction,
                """
                INSERT INTO campaign_rollup_manifest_sources (ManifestId, LineageId, CampaignId, SessionId,
                    ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence)
                SELECT $manifest, $lineage, CampaignId, SessionId, ContributionArtifactId,
                    ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence
                FROM campaign_rollup_sources WHERE RollupArtifactId = $previous;
                """, ct, ("$manifest", Format(prior.Id)), ("$lineage", Format(lineage)),
                ("$previous", Format(legacy.ArtifactId))).ConfigureAwait(false);
        }

        long count = checked((prior?.Count ?? 0) + input.Contributions.Length);

        long ordinal = checked((prior?.Ordinal ?? 0) + 1);

        CovenantDigest priorDigest = prior?.Digest ?? new CovenantDigest(new byte[32]);

        using Manifest hash = SegmentHash(input.CampaignId, lineage, ordinal, priorDigest, count, input.Contributions.Length);

        foreach (CampaignRollupArtifact contribution in input.Contributions.OrderBy(static item => Format(item.SessionId!.Value), StringComparer.Ordinal))
        {
            AppendSource(hash, MetadataFor(contribution));
        }

        Segment segment = new(Guid.NewGuid(), lineage, ordinal, input.CampaignId, 2, priorDigest,
            hash.Finish(), count, input.Contributions.Length);

        await InsertSegmentAsync(connection, transaction, segment, ct).ConfigureAwait(false);

        foreach (CampaignRollupArtifact contribution in input.Contributions)
        {
            _ = await ExecuteAsync(connection, transaction,
                """
                INSERT INTO campaign_rollup_manifest_sources (ManifestId, LineageId, CampaignId, SessionId,
                    ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence)
                VALUES ($manifest, $lineage, $campaign, $session, $contribution, $revision, $content, $sensitivity, $through);
                """, ct, ("$manifest", Format(segment.Id)), ("$lineage", Format(lineage)), ("$campaign", Format(input.CampaignId)),
                ("$session", Format(contribution.SessionId!.Value)), ("$contribution", Format(contribution.ArtifactId)),
                ("$revision", contribution.Revision), ("$content", contribution.ContentDigest.Bytes),
                ("$sensitivity", contribution.SensitivityDigest.Bytes), ("$through", contribution.SummarizedThroughSequence)).ConfigureAwait(false);
        }

        return segment;
    }

    private static async Task InsertSegmentAsync(SqliteConnection connection, SqliteTransaction transaction,
        Segment segment, CancellationToken ct)
    {
        _ = await ExecuteAsync(connection, transaction,
            """
            INSERT INTO campaign_rollup_manifests (ManifestId, LineageId, Ordinal, CampaignId, HashFormatCode,
                PriorManifestDigest, SourceManifestDigest, SourceCount, AddedSourceCount)
            VALUES ($id, $lineage, $ordinal, $campaign, $format, $prior, $digest, $count, $added);
            """, ct, ("$id", Format(segment.Id)), ("$lineage", Format(segment.LineageId)), ("$ordinal", segment.Ordinal),
            ("$campaign", Format(segment.CampaignId)), ("$format", segment.FormatCode), ("$prior", segment.PriorDigest.Bytes),
            ("$digest", segment.Digest.Bytes), ("$count", segment.Count), ("$added", segment.Added)).ConfigureAwait(false);
    }

    private async Task VerifySharedSourcesAsync(SqliteConnection connection, SqliteTransaction transaction,
        Metadata rollup, Segment root, GenerationProvenance provenance, ICovenantSnapshotReadLease? authority, CancellationToken ct)
    {
        long ordinal = 0;

        long count = 0;

        CovenantDigest prior = new(new byte[32]);

        ContentSensitivity sensitivity = ContentSensitivity.None;

        GenerationProvenance actualProvenance = CleanProvenance;

        while (ordinal < root.Ordinal)
        {
            Segment segment;

            await using (SqliteCommand command = Command(connection, transaction,
                """
                SELECT ManifestId, LineageId, Ordinal, CampaignId, HashFormatCode, PriorManifestDigest,
                    SourceManifestDigest, SourceCount, AddedSourceCount
                FROM campaign_rollup_manifests WHERE LineageId = $lineage AND Ordinal = $ordinal;
                """, ("$lineage", Format(root.LineageId)), ("$ordinal", ordinal + 1)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

                segment = await reader.ReadAsync(ct).ConfigureAwait(false)
                    ? ReadSegment(reader) : throw Refuse(Stale("A Campaign provenance prefix is incomplete."));
            }

            if (segment.CampaignId != rollup.CampaignId || segment.PriorDigest != prior
                || segment.Count != checked(count + segment.Added)
                || (segment.FormatCode == 1 && ordinal != 0))
            {
                throw Refuse(Stale("A Campaign provenance segment does not extend its exact prefix."));
            }

            using Manifest hash = segment.FormatCode == 1
                ? new Manifest("Arcanum.CampaignRollup.Sources.v1\0")
                : SegmentHash(segment.CampaignId, segment.LineageId, segment.Ordinal, prior, segment.Count, segment.Added);

            if (segment.FormatCode == 1)
            {
                hash.Guid(segment.CampaignId);
            }

            long added = 0;

            string? after = null;

            while (true)
            {
                List<Source> page = await ReadSourcePageAsync(connection, transaction, segment.Id, after, ct, shared: true).ConfigureAwait(false);

                foreach (Source source in page)
                {
                    (Metadata contribution, GenerationProvenance sourceProvenance) = await VerifySourceAsync(
                        connection, transaction, rollup.CampaignId, source, authority, ct).ConfigureAwait(false);

                    AppendSource(hash, contribution);

                    sensitivity = ContentSensitivityAlgebra.Maximum(sensitivity, contribution.Sensitivity);

                    actualProvenance = actualProvenance.Merge(sourceProvenance);

                    added++;
                }

                if (page.Count < MetadataPageSize)
                {
                    break;
                }

                after = Format(page[^1].SessionId);
            }

            if (segment.FormatCode == 1)
            {
                hash.Number(added);
            }

            if (added != segment.Added || hash.Finish() != segment.Digest)
            {
                throw Refuse(Stale("A Campaign provenance segment's exact source digest changed."));
            }

            count = segment.Count;

            prior = segment.Digest;

            ordinal++;
        }

        if (count != rollup.SourceCount || prior != rollup.SourceManifestDigest
            || sensitivity != rollup.Sensitivity
            || CovenantDigests.Sensitivity(actualProvenance.ToDigestInput(sensitivity)) != rollup.SensitivityDigest
            || CovenantDigests.Sensitivity(provenance.ToDigestInput(sensitivity)) != rollup.SensitivityDigest)
        {
            throw Refuse(Stale("The shared Campaign source closure no longer matches its publication."));
        }
    }

    private async Task<(Metadata Artifact, GenerationProvenance Provenance)> VerifySourceAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid campaignId, Source source,
        ICovenantSnapshotReadLease? authority, CancellationToken ct)
    {
        Metadata contribution = await ReadMetadataAsync(connection, transaction, source.ArtifactId, true, ct).ConfigureAwait(false)
            ?? throw Refuse(Stale("A retained Campaign source revision no longer exists."));

        State? state = await ReadStateAsync(connection, transaction, source.SessionId, true, ct).ConfigureAwait(false);

        Guid? canonical = await ReadCanonicalCampaignAsync(connection, transaction, source.SessionId, ct).ConfigureAwait(false);

        if (source.CampaignId != campaignId || contribution.CampaignId != campaignId
            || contribution.SessionId != source.SessionId || state?.ArtifactId != contribution.ArtifactId || state.RefoldRequired
            || source.Revision != contribution.Revision || source.Revision != state.Revision
            || source.ContentDigest != contribution.ContentDigest || source.SensitivityDigest != contribution.SensitivityDigest
            || source.ThroughSequence != contribution.ThroughSequence || source.ThroughSequence != state.ThroughSequence
            || canonical != campaignId)
        {
            throw Refuse(Stale("A Campaign publication's consumed contribution no longer matches its exact current source."));
        }

        await RevalidateAsync(authority, campaignId, contribution.Sensitivity, ct).ConfigureAwait(false);

        ArtifactSensitivityLabel? label = await VerifyArtifactLabelAsync(connection, transaction, contribution, ct).ConfigureAwait(false);

        return (contribution, label?.Provenance ?? CleanProvenance);
    }
}
