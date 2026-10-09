using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

internal sealed partial class CampaignRollupStore
{
    private async Task VerifySourcesAsync(SqliteConnection connection, SqliteTransaction transaction,
        Metadata rollup, GenerationProvenance provenance, ICovenantSnapshotReadLease? authority, CancellationToken ct)
    {
        using Manifest hash = new("Arcanum.CampaignRollup.Sources.v1\0");

        hash.Guid(rollup.CampaignId);

        long count = 0;

        ContentSensitivity sensitivity = ContentSensitivity.None;

        GenerationProvenance actualProvenance = CleanProvenance;

        string? after = null;

        while (true)
        {
            List<Source> page = await ReadSourcePageAsync(connection, transaction, rollup.ArtifactId, after, ct).ConfigureAwait(false);

            foreach (Source source in page)
            {
                Metadata contribution = await ReadMetadataAsync(connection, transaction, source.ArtifactId, true, ct).ConfigureAwait(false)
                    ?? throw Refuse(Stale("A retained Campaign source revision no longer exists."));

                State? state = await ReadStateAsync(connection, transaction, source.SessionId, true, ct).ConfigureAwait(false);

                Guid? canonical = await ReadCanonicalCampaignAsync(connection, transaction, source.SessionId, ct).ConfigureAwait(false);

                if (source.CampaignId != rollup.CampaignId || contribution.CampaignId != rollup.CampaignId
                    || contribution.SessionId != source.SessionId || state?.ArtifactId != contribution.ArtifactId || state.RefoldRequired
                    || source.Revision != contribution.Revision || source.Revision != state.Revision
                    || source.ContentDigest != contribution.ContentDigest || source.SensitivityDigest != contribution.SensitivityDigest
                    || source.ThroughSequence != contribution.ThroughSequence || source.ThroughSequence != state.ThroughSequence
                    || canonical != rollup.CampaignId)
                {
                    throw Refuse(Stale("A Campaign publication's consumed contribution no longer matches its exact current source."));
                }

                await RevalidateAsync(authority, rollup.CampaignId, contribution.Sensitivity, ct).ConfigureAwait(false);

                ArtifactSensitivityLabel? label = await VerifyArtifactLabelAsync(connection, transaction, contribution, ct).ConfigureAwait(false);

                sensitivity = ContentSensitivityAlgebra.Maximum(sensitivity, contribution.Sensitivity);

                actualProvenance = actualProvenance.Merge(label?.Provenance ?? CleanProvenance);

                AppendSource(hash, contribution);

                count++;
            }

            if (page.Count < MetadataPageSize)
            {
                break;
            }

            after = Format(page[^1].SessionId);
        }

        hash.Number(count);

        if (count != rollup.SourceCount || hash.Finish() != rollup.SourceManifestDigest
            || sensitivity != rollup.Sensitivity
            || CovenantDigests.Sensitivity(actualProvenance.ToDigestInput(sensitivity)) != rollup.SensitivityDigest
            || CovenantDigests.Sensitivity(provenance.ToDigestInput(rollup.Sensitivity)) != rollup.SensitivityDigest)
        {
            throw Refuse(Stale("The complete Campaign source count, manifest, or sensitivity evidence changed."));
        }
    }

    private static async Task<(CovenantDigest Digest, long Count)> FullSourceManifestAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid campaignId, Guid? priorArtifact, ImmutableArray<CampaignRollupArtifact> additions, CancellationToken ct)
    {
        using Manifest hash = new("Arcanum.CampaignRollup.Sources.v1\0");

        hash.Guid(campaignId);

        CampaignRollupArtifact[] next = additions.OrderBy(static artifact => Format(artifact.SessionId!.Value), StringComparer.Ordinal).ToArray();

        int nextIndex = 0;

        long count = 0;

        string? after = null;

        if (priorArtifact is { } prior)
        {
            while (true)
            {
                List<Source> page = await ReadSourcePageAsync(connection, transaction, prior, after, ct).ConfigureAwait(false);

                foreach (Source source in page)
                {
                    string key = Format(source.SessionId);

                    while (nextIndex < next.Length && string.CompareOrdinal(Format(next[nextIndex].SessionId!.Value), key) < 0)
                    {
                        AppendSource(hash, MetadataFor(next[nextIndex++]));

                        count++;
                    }

                    if (nextIndex < next.Length && Format(next[nextIndex].SessionId!.Value) == key)
                    {
                        throw Refuse(Stale("A Campaign fold attempted to consume the same Session twice."));
                    }

                    Metadata metadata = await ReadMetadataAsync(connection, transaction, source.ArtifactId, true, ct).ConfigureAwait(false)
                        ?? throw Refuse(Stale("A Campaign source disappeared during publication."));

                    AppendSource(hash, metadata);

                    count++;
                }

                if (page.Count < MetadataPageSize)
                {
                    break;
                }

                after = Format(page[^1].SessionId);
            }
        }

        while (nextIndex < next.Length)
        {
            AppendSource(hash, MetadataFor(next[nextIndex++]));

            count++;
        }

        hash.Number(count);

        return (hash.Finish(), count);
    }

    private static async Task<List<Source>> ReadSourcePageAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid artifactId, string? after, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction,
            """
            SELECT SessionId, CampaignId, ContributionArtifactId, ContributionRevision, ContentDigest, SensitivityDigest, SummarizedThroughSequence
            FROM campaign_rollup_sources WHERE RollupArtifactId = $artifact AND ($after IS NULL OR SessionId > $after)
            ORDER BY SessionId LIMIT $limit;
            """, ("$artifact", Format(artifactId)), ("$after", after), ("$limit", MetadataPageSize));

        List<Source> page = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            page.Add(new Source(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)),
                reader.GetInt64(3), new CovenantDigest((byte[])reader.GetValue(4)), new CovenantDigest((byte[])reader.GetValue(5)), reader.GetInt64(6)));
        }

        return page;
    }

    private static CovenantDigest ContributionManifest(CampaignContributionInput input)
    {
        using Manifest hash = new("Arcanum.CampaignContribution.Input.v1\0");

        hash.Guid(input.CampaignId);

        hash.Guid(input.SessionId);

        hash.Number(input.ExpectedRevision);

        hash.Number(input.SourceGeneration);

        hash.Number(input.InheritedThroughSequence);

        hash.Number(input.SummarizedThroughSequence);

        hash.OptionalArtifact(input.Previous);

        hash.Number(input.Entries.Length);

        foreach (CampaignContributionEntry entry in input.Entries)
        {
            hash.Guid(entry.EntryId);

            hash.Number(entry.Sequence);

            hash.Number(entry.Role);

            hash.Number(entry.CreatedAtUtc.UtcTicks);

            hash.Digest(entry.ContentDigest);

            hash.Digest(entry.SensitivityDigest);
        }

        return hash.Finish();
    }

    private static CovenantDigest RollupInputManifest(CampaignRollupInput input)
    {
        using Manifest hash = new("Arcanum.CampaignRollup.Input.v1\0");

        hash.Guid(input.CampaignId);

        hash.Number(input.ExpectedRevision);

        hash.Number(input.SourceGeneration);

        hash.OptionalArtifact(input.Previous);

        hash.Number(input.Contributions.Length);

        foreach (CampaignRollupArtifact artifact in input.Contributions)
        {
            hash.Digest(ArtifactIdentityDigest(artifact));
        }

        return hash.Finish();
    }

    private static CovenantDigest ArtifactIdentityDigest(CampaignRollupArtifact artifact)
    {
        using Manifest hash = new("Arcanum.CampaignArtifact.Identity.v1\0");

        hash.Guid(artifact.ArtifactId);

        hash.Guid(artifact.CampaignId);

        hash.Guid(artifact.SessionId ?? Guid.Empty);

        hash.Number(artifact.Revision);

        hash.Number(artifact.SourceGeneration);

        hash.Digest(artifact.ContentDigest);

        hash.Digest(artifact.SensitivityDigest);

        hash.Digest(artifact.SourceManifestDigest);

        hash.Number(artifact.SummarizedThroughSequence);

        hash.Number(artifact.SourceCount);

        return hash.Finish();
    }

    private static void AppendSource(Manifest hash, Metadata artifact)
    {
        hash.Guid(artifact.SessionId!.Value);

        hash.Guid(artifact.CampaignId);

        hash.Guid(artifact.ArtifactId);

        hash.Number(artifact.Revision);

        hash.Number(artifact.SourceGeneration);

        hash.Digest(artifact.ContentDigest);

        hash.Digest(artifact.SensitivityDigest);

        hash.Digest(artifact.SourceManifestDigest);

        hash.Number(artifact.ThroughSequence);
    }

    private static void RequireArtifactShape(CampaignRollupArtifact artifact)
    {
        if (artifact.Content is null || Encoding.UTF8.GetByteCount(artifact.Content) > CampaignRollupLimits.SummaryUtf8Bytes
            || DerivedArtifactContentDigest.ForText(artifact.Content) != artifact.ContentDigest
            || CovenantDigests.Sensitivity(artifact.Provenance.ToDigestInput(artifact.Sensitivity)) != artifact.SensitivityDigest)
        {
            throw Refuse(Stale("A prepared Campaign artifact carries substituted bytes or sensitivity evidence."));
        }
    }

    private static void RequireContributionShape(CampaignContributionInput input)
    {
        if (input.Entries.IsDefaultOrEmpty || input.Entries.Length > MetadataPageSize)
        {
            throw Refuse(Stale("A prepared contribution page has an invalid bounded shape."));
        }

        if (input.Previous is { } previous)
        {
            RequireArtifactShape(previous);
        }

        int bytes = Encoding.UTF8.GetByteCount(input.Previous?.Content ?? string.Empty);

        long after = input.Previous?.SummarizedThroughSequence ?? input.InheritedThroughSequence;

        foreach (CampaignContributionEntry entry in input.Entries)
        {
            if (entry.Content is null || entry.Sequence <= after || entry.Role is not (1 or 2)
                || DerivedArtifactContentDigest.ForText(entry.Content) != entry.ContentDigest
                || CovenantDigests.Sensitivity(entry.Provenance.ToDigestInput(entry.Sensitivity)) != entry.SensitivityDigest)
            {
                throw Refuse(Stale("A prepared contribution Entry carries substituted bytes or sensitivity evidence."));
            }

            bytes = checked(bytes + Encoding.UTF8.GetByteCount(entry.Content));

            after = entry.Sequence;
        }

        if (bytes > CampaignRollupLimits.InputPageUtf8Bytes || after != input.SummarizedThroughSequence)
        {
            throw Refuse(Stale("The prepared contribution page exceeds its bounded cursor or input allocation."));
        }
    }

    private static void RequireRollupShape(CampaignRollupInput input)
    {
        if (input.Contributions.IsDefaultOrEmpty || input.Contributions.Length > MetadataPageSize)
        {
            throw Refuse(Stale("A prepared Campaign fold has an invalid bounded shape."));
        }

        if (input.Previous is { } previous)
        {
            RequireArtifactShape(previous);
        }

        int bytes = Encoding.UTF8.GetByteCount(input.Previous?.Content ?? string.Empty);

        foreach (CampaignRollupArtifact contribution in input.Contributions)
        {
            RequireArtifactShape(contribution);

            bytes = checked(bytes + Encoding.UTF8.GetByteCount(contribution.Content));
        }

        if (bytes > CampaignRollupLimits.InputPageUtf8Bytes)
        {
            throw Refuse(Stale("The prepared Campaign fold exceeds its bounded input allocation."));
        }
    }

    private static Metadata MetadataFor(CampaignRollupArtifact artifact) => new(artifact.ArtifactId, artifact.CampaignId,
        artifact.SessionId, artifact.Revision, artifact.ContentDigest, artifact.Sensitivity, artifact.SensitivityDigest,
        artifact.SourceGeneration, artifact.SourceManifestDigest, artifact.SummarizedThroughSequence, artifact.SourceCount,
        Encoding.UTF8.GetByteCount(artifact.Content));

    private sealed record Source(Guid SessionId, Guid CampaignId, Guid ArtifactId, long Revision,
        CovenantDigest ContentDigest, CovenantDigest SensitivityDigest, long ThroughSequence);

    private sealed class Manifest(string domain) : IDisposable
    {
        private readonly IncrementalHash _hash = Create(domain);

        private static IncrementalHash Create(string domain)
        {
            IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            hash.AppendData(Encoding.UTF8.GetBytes(domain));

            return hash;
        }

        internal void Guid(Guid value) => _hash.AppendData(value.ToByteArray(bigEndian: true));

        internal void Number(long value)
        {
            Span<byte> bytes = stackalloc byte[8];

            BinaryPrimitives.WriteInt64BigEndian(bytes, value);

            _hash.AppendData(bytes);
        }

        internal void Digest(CovenantDigest value) => _hash.AppendData(value.Bytes);

        internal void OptionalArtifact(CampaignRollupArtifact? artifact)
        {
            Number(artifact is null ? 0 : 1);

            if (artifact is not null)
            {
                Digest(ArtifactIdentityDigest(artifact));
            }
        }

        internal CovenantDigest Finish() => new(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }
}
