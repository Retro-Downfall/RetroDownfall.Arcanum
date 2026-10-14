using System.Collections.Immutable;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Telemetry;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    public async Task<Result<LongRestCandidateGroup[]>> DiscoverAsync(Guid generationId, LongRestTarget anchor,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        if (generationId == Guid.Empty || LongRestPolicy.ValidateRequest(new(LongRestTransformationKind.ExactDuplicates, [anchor])).IsFailure)
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.InvalidRequest, "Discovery requires an exact generation and anchor.");
        }

        try
        {
            SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            return await DiscoverWithinAsync(connection, transaction, generationId, anchor, capability, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.Unavailable, "Discovery could not verify its retained source bindings.");
        }
    }

    internal static async Task<Result<LongRestCandidateGroup[]>> DiscoverWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid generationId, LongRestTarget anchor, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction, "GenerationId = $id", Format(generationId), cancellationToken).ConfigureAwait(false);

        if (generation is null || !generation.Published || generation.Phase is not (LongRestSweepPhase.Discover or LongRestSweepPhase.Adjudicate or LongRestSweepPhase.Completed)
            || !await FrozenSourceMetadataMatchesAsync(connection, transaction, generation, cancellationToken).ConfigureAwait(false))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.StaleInput, "Discovery requires a current complete generation.");
        }

        Result authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(authority.Error);
        }

        LongRestSourceLeaf source = await ReadFrozenLeafAsync(connection, transaction, generation, anchor.MemoryId, capability, cancellationToken).ConfigureAwait(false);

        if (source.Target != anchor || !CandidateEligible(source))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.StaleInput, "The anchor is no longer the exact eligible captured version.");
        }

        LongRestLaneBuffer exact = new(LongRestDiscoveryLane.ExactHash);

        LongRestLaneBuffer lexical = new(LongRestDiscoveryLane.Lexical);

        LongRestLaneBuffer vector = new(LongRestDiscoveryLane.Vector);

        float[] direction = SphericalKMeans.NormalizedDirection(source.Vector.ToArray());

        // Every captured leaf is read and verified once per anchor, so the pass also counts the generation's documents.
        long documents = 0;

        string? cursor = null;

        do
        {
            List<string> page = [];

            await using (SqliteCommand command = Command(connection, transaction, """
                SELECT MemoryId FROM long_rest_discovery_leaves
                WHERE GenerationId = $generation AND ($cursor IS NULL OR MemoryId > $cursor)
                ORDER BY MemoryId LIMIT 128;
                """, ("$generation", Format(generationId)), ("$cursor", cursor)))
            {
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    page.Add(reader.GetString(0));
                }
            }

            documents += page.Count;

            foreach (string memory in page)
            {
                LongRestSourceLeaf candidate = await ReadFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false);

                if (memory == anchor.MemoryId || !CandidateEligible(candidate))
                {
                    continue;
                }

                if (StringComparer.OrdinalIgnoreCase.Equals(candidate.Snapshot.ContentHash, source.Snapshot.ContentHash))
                {
                    exact.Add(new(candidate.Target, LongRestDiscoveryLane.ExactHash, 0));
                }

                if (direction.Length > 0 && candidate.Vector.Length == direction.Length)
                {
                    // A candidate below the minimum cosine is never evidence, so it never reaches the lane.
                    double cosine = SphericalKMeans.CosineToDirection(direction, candidate.Vector.AsSpan());

                    if (cosine >= LongRestDiscoveryLimits.MinimumVectorCosine)
                    {
                        vector.Add(new(candidate.Target, LongRestDiscoveryLane.Vector, Math.Clamp(cosine, -1, 1)));
                    }
                }
            }

            cursor = page.Count == 0 ? null : page[^1];
        } while (cursor is not null);

        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms(source.Content!);

        if (terms.Length > 0)
        {
            List<LexicalHit> hits = await LexicalHitsAsync(connection, transaction, generation, anchor.MemoryId, terms, documents, cancellationToken).ConfigureAwait(false);

            // Hits arrive best first. A hit is verified only when the lane could still take it: eligible neighbours stop at the
            // top-K, and an ineligible one that outranks them costs one verification and no slot.
            int verified = 0;

            foreach (LexicalHit hit in hits)
            {
                LongRestSourceLeaf candidate = await ReadFrozenLeafAsync(connection, transaction, generation, hit.MemoryId, capability, cancellationToken).ConfigureAwait(false);

                verified++;

                if (!CandidateEligible(candidate))
                {
                    continue;
                }

                lexical.Add(new(candidate.Target, LongRestDiscoveryLane.Lexical, -(double)hit.Score));

                if (lexical.Count == LongRestDiscoveryLimits.LexicalTopK)
                {
                    break;
                }
            }

            if (verified > 0)
            {
                ArcanumMetrics.LongRestLexicalVerificationsTotal.Add(verified);
            }
        }

        ImmutableArray<LongRestCandidate> hierarchy = await HierarchyCandidatesAsync(connection, transaction, generation, anchor, capability, cancellationToken).ConfigureAwait(false);

        Result<ImmutableArray<LongRestCandidate>> merged = LongRestDiscoveryPolicy.MergeCandidates(anchor,
            exact.Items.Concat(lexical.Items).Concat(vector.Items).Concat(hierarchy));

        if (merged.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(merged.Error);
        }

        authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(authority.Error);
        }

        return LongRestDiscoveryPolicy.GroupCandidates(anchor, merged.Value).Value.ToArray();
    }

    private sealed record LexicalHit(string MemoryId, long Score);

    /// <summary>
    /// The generation's lexical hits for <paramref name="terms"/>, best first, from one MATCH that names the generation as a column
    /// filter. The index is shared by every generation, scope and protected partition, but a column-filtered MATCH reads only this
    /// generation's postings, so which rows match and the document frequency of every term are facts of this generation alone.
    /// Scoring runs in managed integer arithmetic (<see cref="LongRestLexicalScorer"/>), never FTS5's corpus-wide bm25(). A hit
    /// below the recipe's relevance floor is not evidence and is not returned; the anchor never matches itself.
    /// </summary>
    private static async Task<List<LexicalHit>> LexicalHitsAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string anchorMemory, ImmutableArray<string> terms, long documents, CancellationToken cancellationToken)
    {
        string index = generation.Scope.ProtectedPartitionDigest is null ? "long_rest_clean_fts" : "long_rest_protected_fts";

        // The terms are bounded letters and digits, so quoting is complete; the generation is a GUID.
        string match = "GenerationId : \"" + Format(generation.GenerationId) + "\" AND Content : (" + string.Join(" OR ", terms.Select(static term => "\"" + term + "\"")) + ")";

        List<(string MemoryId, LongRestLexicalDocument Document)> matched = [];

        int[] frequencies = new int[terms.Length];

        await using SqliteCommand command = Command(connection, transaction, $"""
            SELECT node.MemoryId, {index}.Content
            FROM {index} JOIN long_rest_discovery_nodes node ON node.NodeId = {index}.NodeId AND node.GenerationId = {index}.GenerationId
            WHERE {index} MATCH $match AND node.GenerationId = $generation AND node.Layer = 0;
            """, ("$match", match), ("$generation", Format(generation.GenerationId)));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            LongRestLexicalDocument document = LongRestLexicalScorer.Profile(reader.GetString(1), terms);

            for (int term = 0; term < terms.Length; term++)
            {
                if (document.Frequencies[term] > 0)
                {
                    frequencies[term]++;
                }
            }

            matched.Add((reader.GetString(0), document));
        }

        ImmutableArray<int> weights = LongRestLexicalScorer.Weights(documents, [.. frequencies]);

        List<LexicalHit> hits = [];

        foreach ((string memory, LongRestLexicalDocument document) in matched)
        {
            long score = LongRestLexicalScorer.Score(weights, document);

            if (memory != anchorMemory && score >= generation.Recipe.LexicalMinimumScore)
            {
                hits.Add(new(memory, score));
            }
        }

        hits.Sort(static (left, right) => left.Score != right.Score ? right.Score.CompareTo(left.Score) : StringComparer.Ordinal.Compare(left.MemoryId, right.MemoryId));

        return hits;
    }

    private static bool CandidateEligible(LongRestSourceLeaf leaf) => leaf.Snapshot.IsCurrent
        && !leaf.Snapshot.IsConsolidated && leaf.Snapshot.PinnedAtUtc is null && leaf.Snapshot.RetiredAtUtc is null;

    private static async Task<LongRestSourceLeaf> ReadFrozenLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken) =>
        (await ReadVerifiedFrozenLeafAsync(connection, transaction, generation, memory, capability, cancellationToken).ConfigureAwait(false)).Leaf;

    private sealed record VerifiedFrozenLeaf(LongRestSourceLeaf Leaf, CovenantDigest OriginalSourceDigest);

    private static async Task<VerifiedFrozenLeaf> ReadVerifiedFrozenLeafAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string memory, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken)
    {
        SourceCoordinate coordinate;

        CovenantDigest expected;

        LongRestSnapshot frozen;

        await using (SqliteCommand command = Command(connection, transaction, """
            SELECT leaf.InsertionSequence, leaf.MutationEpoch, leaf.VersionId, leaf.SourceDigest, leaf.SnapshotJson
            FROM long_rest_discovery_leaves leaf JOIN long_rest_sources current ON current.MemoryId = leaf.MemoryId
            WHERE leaf.GenerationId = $generation AND leaf.MemoryId = $memory
                AND current.InsertionSequence = leaf.InsertionSequence AND current.MutationEpoch = leaf.MutationEpoch;
            """, ("$generation", Format(generation.GenerationId)), ("$memory", memory)))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("A captured source coordinate is no longer current.");
            }

            coordinate = new(memory, reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2));

            expected = new((byte[])reader[3]);

            frozen = JsonSerializer.Deserialize(reader.GetString(4), LongRestJsonContext.Default.LongRestSnapshot) ?? throw new InvalidDataException();
        }

        Result<LongRestSourceLeaf> loaded = await ReadSourceAsync(connection, transaction, generation, coordinate, capability, true, cancellationToken).ConfigureAwait(false);

        if (loaded.IsFailure)
        {
            throw new InvalidDataException(loaded.Error.Code);
        }

        Result<CovenantDigest> actual = LongRestDiscoveryHash.SourcePage(generation.Scope, [loaded.Value]);

        if (actual.IsFailure || actual.Value != expected)
        {
            LongRestSourceLeaf comparable = loaded.Value with
            {
                Snapshot = loaded.Value.Snapshot with { IsConsolidated = frozen.IsConsolidated, IsTransformationOutput = frozen.IsTransformationOutput },
            };

            if (LongRestDiscoveryHash.SourcePage(generation.Scope, [comparable]).Value != expected)
            {
                throw new InvalidDataException("The captured source bytes or complete decision state changed.");
            }

            await VerifyOwnProjectionAsync(connection, transaction, generation, loaded.Value, frozen, cancellationToken).ConfigureAwait(false);
        }

        return new(loaded.Value, expected);
    }
}
