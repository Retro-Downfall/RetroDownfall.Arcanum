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

            // Discovery only reads, so it takes no writer lock; the sweep that reads and queues work in one transaction calls DiscoverWithinAsync
            // from inside its own writer transaction instead.
            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

            LongRestVerificationCounters.Transaction(deferred: true);

            return await DiscoverWithinAsync(connection, transaction, generationId, anchor, capability, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestCandidateGroup[]>(ErrorCodes.LongRest.Unavailable, "Discovery could not verify its retained source bindings.");
        }
    }

    /// <param name="spreads">
    /// Present when the caller reads many anchors of one generation (the sweep): the spread of the generation's sample does not depend on the
    /// anchor, so the first anchor of each vector width reads it from the leaves and the others are handed it. Every anchor still reads and
    /// verifies every leaf; only the arithmetic over the sample is shared.
    /// </param>
    internal static async Task<Result<LongRestCandidateGroup[]>> DiscoverWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid generationId, LongRestTarget anchor, LongRestMaintenanceCapability? capability, CancellationToken cancellationToken,
        LongRestSpreadCache? spreads = null)
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

        // The pass reads every captured leaf once per anchor, in memory id order, so it also reads the anchor's Background Floor from them: the
        // median of the anchor's cosine to every other leaf, eligible or not, and the spread of a fixed sample of the generation's leaves.
        int width = source.Vector.IsDefaultOrEmpty ? 0 : source.Vector.Length;

        double? shared = spreads is not null && width > 0 ? spreads.Share(generationId, width, null) : null;

        LongRestBackgroundSampler background = new(source.Vector, shared);

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

                // Every leaf the generation froze counts toward the floor, whether or not the sweep has since consolidated it.
                double cosine = background.Observe(memory == anchor.MemoryId, candidate.Vector);

                if (memory == anchor.MemoryId || !CandidateEligible(candidate))
                {
                    continue;
                }

                if (StringComparer.OrdinalIgnoreCase.Equals(candidate.Snapshot.ContentHash, source.Snapshot.ContentHash))
                {
                    exact.Add(new(candidate.Target, LongRestDiscoveryLane.ExactHash, 0));
                }

                if (!double.IsNaN(cosine))
                {
                    // The lane keeps its nearest eight; the floor is known only once the pass is done, and the nearest eight that clear it
                    // are the nearest eight of all with those below it dropped, so nothing is lost by dropping them afterwards.
                    vector.Add(new(candidate.Target, LongRestDiscoveryLane.Vector, Math.Clamp(cosine, -1, 1)));
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

                LongRestVerificationCounters.FtsHitVerification();

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

        // The anchor's Background Floor travels with its groups: the packer judges every nominee, lexical and hierarchical ones included, by it.
        int floor = background.FloorPermille(out double? read);

        if (spreads is not null && shared is null && read is { } measured)
        {
            _ = spreads.Share(generationId, width, measured);
        }

        Result<ImmutableArray<LongRestCandidate>> merged = LongRestDiscoveryPolicy.MergeCandidates(anchor,
            exact.Items.Concat(lexical.Items).Concat(vector.Items.Where(item => LongRestPairing.Clears(item.Score, floor))).Concat(hierarchy));

        if (merged.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(merged.Error);
        }

        authority = await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false);

        if (authority.IsFailure)
        {
            return Result<LongRestCandidateGroup[]>.Failure(authority.Error);
        }

        return LongRestDiscoveryPolicy.GroupCandidates(anchor, merged.Value, floor).Value.ToArray();
    }

    private sealed record LexicalHit(string MemoryId, long Score);

    /// <summary>
    /// The generation's lexical hits for <paramref name="terms"/>, best first, from one MATCH built by
    /// <see cref="LongRestLexicalScorer.MatchExpression"/>. The index is shared by every generation, scope and protected partition. What
    /// keeps another generation's rows out is the join to this generation's own leaf nodes, which matches on the generation and the node,
    /// and the document counts and term frequencies below, which are taken over those joined rows alone; the column filter in the
    /// expression bounds the matched set to the generation. Scoring runs in managed integer arithmetic (<see cref="LongRestLexicalScorer"/>),
    /// never FTS5's corpus-wide bm25(). A hit below the recipe's relevance floor is not evidence and is not returned; the anchor never
    /// matches itself.
    /// </summary>
    private static async Task<List<LexicalHit>> LexicalHitsAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, string anchorMemory, ImmutableArray<string> terms, long documents, CancellationToken cancellationToken)
    {
        string index = generation.Scope.ProtectedPartitionDigest is null ? "long_rest_clean_fts" : "long_rest_protected_fts";

        string match = LongRestLexicalScorer.MatchExpression(generation.GenerationId, terms);

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

        List<LexicalHit> scored = [];

        // What the anchor scores against itself under these weights is the most any document can cover of it.
        long self = 0;

        foreach ((string memory, LongRestLexicalDocument document) in matched)
        {
            long score = LongRestLexicalScorer.Score(weights, document);

            if (memory == anchorMemory)
            {
                self = score;
            }
            else
            {
                scored.Add(new(memory, score));
            }
        }

        // A hit is nominated when it covers at least the recipe's share of the anchor's own score. The share is of the anchor's score, not
        // a fixed number, so it does not drift with the size of the corpus; an anchor whose own terms weigh nothing (every one is in every
        // memory) nominates nothing, and a hit that scores nothing is never a hit.
        foreach (LexicalHit hit in scored)
        {
            if (self > 0 && hit.Score > 0 && hit.Score * 1000 >= self * generation.Recipe.LexicalMinimumCoveragePermille)
            {
                hits.Add(hit);
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
        LongRestVerificationCounters.FullLeafRead();

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

/// <summary>
/// The spread of a generation's sample of frozen vectors, for each vector width, as the first anchor of that width read it. Every anchor of
/// a generation reads the same spread (see <see cref="LongRestBackgroundSampler"/>), and the sweep reads one anchor to a transaction, so it
/// is kept for the next one instead of being computed again under the writer lock. A generation's frozen leaves change only when the
/// generation is invalidated or deleted, and every anchor's pass still reads and digest-verifies every leaf and refuses a generation that is
/// no longer current before it asks for the spread, so a remembered value is never read for leaves that have changed. The key is the exact
/// generation and the vector width: the two things the spread is a function of. The cache is small and drops its oldest entry first.
/// </summary>
internal sealed class LongRestSpreadCache
{
    private const int Capacity = 8;

    private readonly Lock _gate = new();

    private readonly Dictionary<(Guid Generation, int Width), double> _spreads = [];

    private readonly Queue<(Guid Generation, int Width)> _order = new();

    /// <summary>
    /// The spread remembered for the generation and width, or <see langword="null"/> when none is. A spread an anchor has just read is
    /// remembered by passing it as <paramref name="read"/>; the first one stands, because every reading of the same leaves is the same bits
    /// and a cache never replaces what an anchor already read.
    /// </summary>
    internal double? Share(Guid generation, int width, double? read)
    {
        lock (_gate)
        {
            if (_spreads.TryGetValue((generation, width), out double known))
            {
                return known;
            }

            if (read is not { } measured)
            {
                return null;
            }

            _spreads[(generation, width)] = measured;

            _order.Enqueue((generation, width));

            if (_order.Count > Capacity)
            {
                _ = _spreads.Remove(_order.Dequeue());
            }

            return measured;
        }
    }
}
