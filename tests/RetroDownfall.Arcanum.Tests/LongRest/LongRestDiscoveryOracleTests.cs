using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave.Tapestry;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Differential oracle for the discovery scan. The production scan reads the lexical hits of one column-filtered MATCH lazily, in score
// order, and streams the vector and exact lanes through bounded buffers with a span-based cosine. The reference below is the plain
// version of the same specification: it reads every captured leaf, ranks each lane by merging candidates through the public policy one
// at a time (the previous implementation's vector and exact lanes, kept here verbatim) and scores the lexical lane over every leaf
// with no index at all. Over seeded random corpora the two must produce identical groups and evidence, scores included. Pinned and
// retired memories are part of the corpus the sweep runs over but are never captured, so they are in no generation: the generation's
// document count, every document frequency and every lane are those of the eligible memories alone, and no group ever names one.
public sealed class LongRestDiscoveryOracleTests
{
    private const int Width = 16;

    private sealed record Corpus(string[] Ids, string[] Texts, float[][] Vectors, HashSet<int> Pinned, HashSet<int> Retired, int[] Anchors);

    private static readonly string[] Words = [.. Enumerable.Range(0, 70).Select(static index => "word" + index)];

    private static string Id(int index, bool guid) => guid ? $"00000000-0000-4000-8000-{index + 1:D12}" : $"m-{index:D3}";

    private static float[] Unit(Random random, int width)
    {
        float[] vector = new float[width];

        double norm = 0;

        for (int index = 0; index < width; index++)
        {
            vector[index] = (float)(random.NextDouble() * 2 - 1);

            norm += vector[index] * vector[index];
        }

        norm = Math.Sqrt(norm);

        for (int index = 0; index < width; index++) vector[index] = (float)(vector[index] / norm);

        return vector;
    }

    // A vector at the given cosine to a unit vector, in a random orthogonal direction, scaled by an arbitrary magnitude.
    private static float[] AtCosine(Random random, float[] unit, double cosine, double scale)
    {
        float[] other = Unit(random, unit.Length);

        double dot = 0;

        for (int index = 0; index < unit.Length; index++) dot += (double)other[index] * unit[index];

        double norm = 0;

        float[] orthogonal = new float[unit.Length];

        for (int index = 0; index < unit.Length; index++)
        {
            orthogonal[index] = (float)(other[index] - dot * unit[index]);

            norm += (double)orthogonal[index] * orthogonal[index];
        }

        norm = Math.Sqrt(norm);

        double sine = Math.Sqrt(1 - cosine * cosine);

        float[] result = new float[unit.Length];

        for (int index = 0; index < unit.Length; index++) result[index] = (float)(scale * (cosine * unit[index] + sine * orthogonal[index] / norm));

        return result;
    }

    private static string Text(Random random, int words)
    {
        List<string> parts = [];

        for (int index = 0; index < words; index++)
        {
            string word = Words[(int)(Words.Length * Math.Pow(random.NextDouble(), 2.2))];

            parts.Add(random.Next(4) switch { 0 => word.ToUpperInvariant(), 1 => char.ToUpperInvariant(word[0]) + word[1..], _ => word });
        }

        return string.Join(random.Next(2) == 0 ? " " : ", ", parts);
    }

    private static Corpus Generate(int seed, int count)
    {
        Random random = new(seed);

        float[][] bases = [.. Enumerable.Range(0, 6).Select(_ => Unit(random, Width))];

        List<string> texts = [];

        List<float[]> vectors = [];

        HashSet<int> pinned = [];

        HashSet<int> retired = [];

        List<int> anchors = [];

        for (int index = 0; index < count; index++)
        {
            texts.Add(Text(random, random.Next(3, 10)));

            float[] vector = AtCosine(random, bases[random.Next(bases.Length)], 0.35 + random.NextDouble() * 0.65, 0.5 + random.NextDouble() * 5);

            vectors.Add(index % 29 == 28 ? [(float)random.NextDouble() + 0.1f, 0.2f, 0.3f, 0.4f] : vector);
        }

        // Exact duplicates of text, and of vector.
        for (int copy = 0; copy < count / 12; copy++)
        {
            int source = random.Next(count / 2);

            int target = count / 2 + random.Next(count / 2);

            texts[target] = texts[source];

            if (copy % 2 == 0 && vectors[source].Length == Width) vectors[target] = [.. vectors[source]];
        }

        // Designated anchors, each with neighbours built to sit on the cut lines of every lane.
        for (int anchor = 0; anchor < 8; anchor++)
        {
            int index = anchor * 3;

            anchors.Add(index);

            vectors[index] = AtCosine(random, bases[anchor % bases.Length], 0.9, 1 + random.NextDouble() * 3);

            float[] unit = SphericalKMeans.NormalizedDirection(vectors[index]);

            // Near duplicates just above and just below the 0.92 floor (down to 5e-5 either side of it), and exact vector ties, all at distinct magnitudes.
            for (int offset = 0; offset < 16; offset++)
            {
                int neighbour = 30 + anchor * 16 + offset;

                vectors[neighbour] = offset switch
                {
                    < 3 => AtCosine(random, unit, 0.9215, 0.5 + offset),
                    < 6 => AtCosine(random, unit, 0.9185, 0.5 + offset),
                    < 9 => [.. vectors[index]],
                    < 12 => AtCosine(random, unit, 0.99, 2),
                    < 14 => AtCosine(random, unit, 0.92005, 0.5 + offset),
                    _ => AtCosine(random, unit, 0.91995, 0.5 + offset),
                };

                // Lexical neighbours: a growing overlap with the anchor's own text, so the lane has a real order with ties at its cut.
                string[] own = texts[index].Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);

                texts[neighbour] = string.Join(' ', own.Take(1 + offset % 6)) + " extra" + offset;

                // Neighbours that are ineligible and would outrank the eligible ones: more overlap than any of them.
                if (offset < 12 && offset % 4 == 3)
                {
                    pinned.Add(neighbour);

                    texts[neighbour] = texts[index] + " pinned" + offset;
                }
                else if (offset < 12 && offset % 4 == 2 && anchor % 2 == 0)
                {
                    retired.Add(neighbour);

                    texts[neighbour] = texts[index] + " retired" + offset;
                }
            }
        }

        // Exact duplicates of every designated anchor's text, two among its neighbours (one with the anchor's own vector, one just above the
        // floor), so the exact lane has evidence in every corpus and is compared with the reference whatever the random duplicates did.
        foreach (int anchor in anchors)
        {
            texts[30 + anchor / 3 * 16 + 8] = texts[anchor];

            texts[30 + anchor / 3 * 16 + 12] = texts[anchor];
        }

        // And, in a corpus large enough, more exact duplicates of the first anchor than the exact lane keeps, all tied at the cut, so the
        // lane buffer's eviction and tie-break are compared with the reference too.
        if (count >= 300)
        {
            for (int copy = 170; copy < 215; copy++) texts[copy] = texts[anchors[0]];
        }

        return new([.. Enumerable.Range(0, count).Select(index => Id(index, false))], [.. texts], [.. vectors], pinned, retired, [.. anchors]);
    }

    private static bool Eligible(LongRestSourceLeaf leaf) => leaf.Snapshot.IsCurrent
        && !leaf.Snapshot.IsConsolidated && leaf.Snapshot.PinnedAtUtc is null && leaf.Snapshot.RetiredAtUtc is null;

    // The reference discovery: every leaf is read, and nothing is streamed, bounded or lazy.
    private static async Task<LongRestCandidateGroup[]> ReferenceAsync(LongRestSweepHarness h, LongRestDiscoveryGeneration generation, string anchorMemory,
        LongRestMaintenanceCapability? capability)
    {
        await using Microsoft.Data.Sqlite.SqliteTransaction transaction = h.Connection.BeginTransaction();

        List<string> memories = [];

        await using (Microsoft.Data.Sqlite.SqliteCommand command = h.Connection.CreateCommand())
        {
            command.Transaction = transaction;

            command.CommandText = "SELECT MemoryId FROM long_rest_discovery_leaves WHERE GenerationId = $generation ORDER BY MemoryId;";

            command.Parameters.AddWithValue("$generation", generation.GenerationId.ToString("D").ToUpperInvariant());

            await using Microsoft.Data.Sqlite.SqliteDataReader reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync()) memories.Add(reader.GetString(0));
        }

        Dictionary<string, LongRestSourceLeaf> leaves = [];

        foreach (string memory in memories)
        {
            leaves[memory] = await LongRestDiscoveryStore.ReadSweepLeafAsync(h.Connection, transaction, generation, memory, capability, CancellationToken.None);
        }

        LongRestSourceLeaf source = leaves[anchorMemory];

        LongRestTarget anchor = source.Target;

        // The previous implementation's exact and vector lanes: one public merge per candidate.
        ImmutableArray<LongRestCandidate>[] lanes = [[], [], [], []];

        float[] direction = SphericalKMeans.NormalizedDirection(source.Vector.ToArray());

        foreach (string memory in memories)
        {
            LongRestSourceLeaf candidate = leaves[memory];

            if (memory == anchorMemory || !Eligible(candidate)) continue;

            if (StringComparer.OrdinalIgnoreCase.Equals(candidate.Snapshot.ContentHash, source.Snapshot.ContentHash))
            {
                Rank(new(candidate.Target, LongRestDiscoveryLane.ExactHash, 0));
            }

            if (direction.Length > 0 && candidate.Vector.Length == direction.Length)
            {
                float[] other = SphericalKMeans.NormalizedDirection(candidate.Vector.ToArray());

                if (other.Length == direction.Length)
                {
                    Rank(new(candidate.Target, LongRestDiscoveryLane.Vector, Math.Clamp(SphericalKMeans.DirectionCosine(direction, other), -1, 1)));
                }
            }
        }

        // The lexical lane over every leaf, with no index: documents and document frequencies are the generation's own.
        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms(source.Content!);

        if (terms.Length > 0)
        {
            LongRestLexicalDocument[] documents = [.. memories.Select(memory => LongRestLexicalScorer.Profile(leaves[memory].Content!, terms))];

            int[] frequencies = new int[terms.Length];

            foreach (LongRestLexicalDocument document in documents)
            {
                for (int term = 0; term < terms.Length; term++)
                {
                    if (document.Frequencies[term] > 0) frequencies[term]++;
                }
            }

            ImmutableArray<int> weights = LongRestLexicalScorer.Weights(memories.Count, [.. frequencies]);

            var ranked = memories.Select((memory, index) => (Memory: memory, Score: LongRestLexicalScorer.Score(weights, documents[index])))
                .Where(item => item.Memory != anchorMemory && item.Score >= LongRestDiscoveryLimits.LexicalMinimumScore)
                .OrderByDescending(static item => item.Score).ThenBy(static item => item.Memory, StringComparer.Ordinal).ToArray();

            int eligible = 0;

            foreach (var item in ranked)
            {
                if (!Eligible(leaves[item.Memory])) continue;

                Rank(new(leaves[item.Memory].Target, LongRestDiscoveryLane.Lexical, -(double)item.Score));

                if (++eligible == LongRestDiscoveryLimits.LexicalTopK) break;
            }
        }

        ImmutableArray<LongRestCandidate> merged = LongRestSweepHarness.Require(LongRestDiscoveryPolicy.MergeCandidates(anchor, lanes.SelectMany(static lane => lane)));

        return LongRestSweepHarness.Require(LongRestDiscoveryPolicy.GroupCandidates(anchor, merged)).ToArray();

        void Rank(LongRestCandidate candidate)
        {
            int lane = (int)candidate.Lane - 1;

            lanes[lane] = LongRestSweepHarness.Require(LongRestDiscoveryPolicy.MergeCandidates(anchor, lanes[lane].Append(candidate)));
        }
    }

    [SkippableTheory]
    [InlineData(11, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(21, true)]
    public async Task Discovery_equals_the_brute_force_reference_over_seeded_corpora(int seed, bool protectedPartition)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        int count = protectedPartition ? 170 : 330;

        Corpus corpus = Generate(seed, count);

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        CovenantDigest? partition = null;

        for (int index = 0; index < count; index++)
        {
            string id = Id(index, protectedPartition);

            await h.SeedAsync(id, corpus.Texts[index]);

            await h.SetEmbeddingAsync(id, corpus.Vectors[index]);

            if (corpus.Pinned.Contains(index))
            {
                await h.ExecuteAsync("UPDATE saga_memories SET PinnedAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = $id;", ("$id", id));
            }
            else if (corpus.Retired.Contains(index))
            {
                await h.ExecuteAsync("UPDATE saga_memories SET RetiredAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id = $id;", ("$id", id));
            }

            if (protectedPartition) partition = await h.ProtectAsync(Guid.Parse(id), provenance);
        }

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        LongRestDiscoveryGeneration generation = await h.CaptureAsync(partition: partition, authority: protectedPartition ? authority : null);

        LongRestMaintenanceCapability? capability = h.Capability;

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1 WHERE GenerationId = $generation;",
            ("$generation", generation.GenerationId.ToString("D").ToUpperInvariant()));

        string[] excluded = [.. corpus.Pinned.Concat(corpus.Retired).Select(index => Id(index, protectedPartition))];

        Assert.True(excluded.Length >= 10, $"pinned and retired memories: {excluded.Length}");

        Assert.Equal((long)(count - excluded.Length), await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves;"));

        foreach (string memory in excluded)
        {
            Assert.Equal(0L, await h.ScalarAsync($"SELECT count(*) FROM long_rest_discovery_leaves WHERE MemoryId = '{memory}';"));
        }

        string report = string.Empty;

        int fullLexical = 0;

        int vectorEvidence = 0;

        int exactEvidence = 0;

        foreach (int anchorIndex in corpus.Anchors)
        {
            string anchor = Id(anchorIndex, protectedPartition);

            LongRestCandidateGroup[] actual = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), capability, CancellationToken.None));

            LongRestCandidateGroup[] expected = await ReferenceAsync(h, generation, anchor, capability);

            Assert.Equal(expected.Length, actual.Length);

            for (int group = 0; group < expected.Length; group++)
            {
                Assert.Equal(expected[group].Targets.ToArray(), actual[group].Targets.ToArray());

                Assert.Equal(expected[group].Evidence.ToArray(), actual[group].Evidence.ToArray());
            }

            LongRestCandidate[] evidence = [.. actual.SelectMany(static group => group.Evidence)];

            Assert.Empty(evidence.Select(static candidate => candidate.Target.MemoryId).Intersect(excluded));

            report += $"[{anchor}: x{evidence.Count(static c => c.Lane == LongRestDiscoveryLane.ExactHash)} l{evidence.Count(static c => c.Lane == LongRestDiscoveryLane.Lexical)} v{evidence.Count(static c => c.Lane == LongRestDiscoveryLane.Vector)} h{evidence.Count(static c => c.Lane == LongRestDiscoveryLane.Hierarchy)} g{actual.Length}]";

            // An exact duplicate is also the best lexical hit there is, but the merge keeps a target once, under the exact lane: the lexical lane
            // was full when its evidence and the duplicates it handed to the exact lane add up to its bound.
            if (evidence.Count(static candidate => candidate.Lane is LongRestDiscoveryLane.Lexical or LongRestDiscoveryLane.ExactHash) >= LongRestDiscoveryLimits.LexicalTopK
                && evidence.Any(static candidate => candidate.Lane == LongRestDiscoveryLane.Lexical)) fullLexical++;

            vectorEvidence += evidence.Count(static candidate => candidate.Lane == LongRestDiscoveryLane.Vector);

            exactEvidence += evidence.Count(static candidate => candidate.Lane == LongRestDiscoveryLane.ExactHash);
        }

        // The corpus really exercises the cut lines: full lexical lanes, vector evidence from both sides of the floor, and exact duplicates.
        Assert.True(fullLexical >= 3, $"full lexical lanes: {fullLexical} {report}");

        Assert.True(vectorEvidence >= 8, $"vector evidence: {vectorEvidence}");

        Assert.True(exactEvidence >= corpus.Anchors.Length * 2, $"exact evidence: {exactEvidence}");

        if (!protectedPartition)
        {
            Assert.True(corpus.Texts.Count(text => text == corpus.Texts[corpus.Anchors[0]]) > LongRestDiscoveryLimits.MergedNeighbors, "exact duplicates beyond the lane bound");
        }
    }
}
