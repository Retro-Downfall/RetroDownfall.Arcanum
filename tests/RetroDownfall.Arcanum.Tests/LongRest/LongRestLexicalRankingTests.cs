using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

// Scoped Lexical Ranking. A lexical score is a pure function of one generation's own facts, a hit must clear the recipe's relevance
// floor, and only the hits the lane can still take are verified.
public sealed class LongRestLexicalRankingTests
{
    private const string LexicalMetric = "arcanum.long_rest.discovery.lexical_verifications";

    private static readonly (string Name, string Text)[] Corpus =
    [
        ("anchor", "Amber opal quartz in the vault"),
        ("a-1", "opal quartz geode"),
        ("a-2", "opal"),
        ("a-3", "opal opal opal gem sapphire in vault"),
        ("a-4", "the quartz vault"),
        ("a-5", "AMBER amber ember"),
        ("a-6", "ruby garnet"),
        .. Enumerable.Range(0, 24).Select(static index => ($"b-{index:D2}", $"zeta{index} theta{index} kappa{index}")),
    ];

    private static string Id(int index) => $"00000000-0000-4000-8000-{index + 1:D12}";

    private static float[] OneHot(int width, int index)
    {
        float[] vector = new float[width];

        vector[index] = 1;

        return vector;
    }

    // The independent expectation: tokenise, count document frequencies over exactly this generation's texts, weigh, score, apply the
    // floor and the top-K, order by score and then memory id.
    private static (string Name, double Score)[] Expected(IReadOnlyList<(string Name, string Text)> generation)
    {
        ImmutableArray<string> terms = LongRestLexicalScorer.QueryTerms(generation.Single(static item => item.Name == "anchor").Text);

        LongRestLexicalDocument[] documents = [.. generation.Select(item => LongRestLexicalScorer.Profile(item.Text, terms))];

        int[] frequencies = new int[terms.Length];

        foreach (LongRestLexicalDocument document in documents)
        {
            for (int term = 0; term < terms.Length; term++)
            {
                if (document.Frequencies[term] > 0) frequencies[term]++;
            }
        }

        ImmutableArray<int> weights = LongRestLexicalScorer.Weights(generation.Count, [.. frequencies]);

        return [.. generation.Select((item, index) => (item.Name, Score: LongRestLexicalScorer.Score(weights, documents[index])))
            .Where(item => item.Name != "anchor" && item.Score >= LongRestDiscoveryLimits.LexicalMinimumScore)
            .OrderByDescending(static item => item.Score).ThenBy(static item => item.Name, StringComparer.Ordinal)
            .Take(LongRestDiscoveryLimits.LexicalTopK).Select(static item => (item.Name, -(double)item.Score))];
    }

    private static string[] Lexical(LongRestCandidateGroup[] groups, Func<string, string> name) => [.. groups.SelectMany(static group => group.Evidence)
        .Where(static candidate => candidate.Lane == LongRestDiscoveryLane.Lexical).Select(candidate => $"{name(candidate.Target.MemoryId)}={candidate.Score:R}")];

    [SkippableTheory]
    [InlineData(false, "retained-generation")]
    [InlineData(false, "other-campaign")]
    [InlineData(false, "foreign-rows")]
    [InlineData(true, "retained-generation")]
    [InlineData(true, "other-partition")]
    [InlineData(true, "foreign-rows")]
    public async Task Lexical_ranking_ignores_other_generations_scopes_and_partitions(bool protectedTarget, string flood)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Dictionary<string, string> names = [];

        for (int index = 0; index < Corpus.Length; index++)
        {
            string id = protectedTarget ? Id(index) : Corpus[index].Name;

            names[id] = Corpus[index].Name;

            await h.SeedAsync(id, Corpus[index].Text);

            await h.SetEmbeddingAsync(id, OneHot(Corpus.Length, index));
        }

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        CovenantDigest? partition = null;

        if (protectedTarget)
        {
            foreach (string id in names.Keys) partition = await h.ProtectAsync(Guid.Parse(id), provenance);
        }

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        if (flood == "retained-generation")
        {
            // An older generation of the very same scope and corpus, kept after it completed: its rows sit in the same index.
            LongRestDiscoveryGeneration retained = await h.CaptureAsync(partition: partition, authority: protectedTarget ? authority : null);

            await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 6 WHERE GenerationId = $generation;", ("$generation", retained.GenerationId.ToString("D").ToUpperInvariant()));

            Assert.Equal((long)Corpus.Length, await h.ScalarAsync($"SELECT count(*) FROM {(protectedTarget ? "long_rest_protected_fts" : "long_rest_clean_fts")};"));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync(partition: partition, authority: protectedTarget ? authority : null);

        if (flood == "retained-generation")
        {
            Assert.Equal(2L * Corpus.Length, await h.ScalarAsync($"SELECT count(*) FROM {(protectedTarget ? "long_rest_protected_fts" : "long_rest_clean_fts")};"));
        }

        LongRestMaintenanceCapability? capability = h.Capability;

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1 WHERE GenerationId = $generation;",
            ("$generation", generation.GenerationId.ToString("D").ToUpperInvariant()));

        string anchor = names.Single(static pair => pair.Value == "anchor").Key;

        Func<string, string> readable = id => names.TryGetValue(id, out string? name) ? name : id;

        LongRestCandidateGroup[] before = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), capability, CancellationToken.None));

        // The lane's evidence is the independent expectation: this generation's documents alone decide it.
        string[] expected = [.. Expected(Corpus).Select(item => $"{item.Name}={item.Score:R}")];

        Assert.Equal(expected, Lexical(before, readable));

        Assert.True(expected.Length >= 4);

        await FloodAsync(h, flood, protectedTarget, partition, authority);

        LongRestCandidateGroup[] after = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync(anchor), capability, CancellationToken.None));

        Assert.Equal(expected, Lexical(after, readable));

        Assert.Equal(before.Select(static group => group.Targets.Select(static target => target.MemoryId)), after.Select(static group => group.Targets.Select(static target => target.MemoryId)));

        Assert.Equal(before.SelectMany(static group => group.Evidence).ToArray(), after.SelectMany(static group => group.Evidence).ToArray());
    }

    private static async Task FloodAsync(LongRestSweepHarness h, string flood, bool protectedTarget, CovenantDigest? partition, CovenantTurnLease authority)
    {
        string flooded = "amber opal quartz in the vault opal opal quartz geode";

        if (flood == "foreign-rows")
        {
            // Rows of generations that do not exist, in both indexes: term statistics of the whole table now say 'opal' is everywhere.
            for (int index = 0; index < 300; index++)
            {
                foreach (string table in new[] { "long_rest_clean_fts", "long_rest_protected_fts" })
                {
                    await h.ExecuteAsync($"INSERT INTO {table} (NodeId, GenerationId, Content) VALUES ($node, $generation, $content);",
                        ("$node", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("$generation", Guid.NewGuid().ToString("D").ToUpperInvariant()), ("$content", flooded));
                }
            }

            return;
        }

        if (flood == "retained-generation")
        {
            return;
        }

        Guid? campaign = flood == "other-campaign" ? Guid.NewGuid() : null;

        Guid? session = campaign is null ? null : await h.SessionAsync(campaign);

        CovenantDigest? otherPartition = partition;

        if (flood == "other-partition")
        {
            GenerationProvenance other = GenerationProvenance.CreateExact([Guid.NewGuid()]);

            for (int index = 0; index < 40; index++)
            {
                string id = Id(1000 + index);

                await h.SeedAsync(id, flooded);

                await h.SetEmbeddingAsync(id, OneHot(Corpus.Length + 40, Corpus.Length + index));

                otherPartition = await h.ProtectAsync(Guid.Parse(id), other);
            }
        }
        else if (flood == "other-campaign")
        {
            for (int index = 0; index < 40; index++)
            {
                string id = "campaign-" + index;

                await h.SeedAsync(id, flooded, session);

                await h.SetEmbeddingAsync(id, OneHot(Corpus.Length + 40, Corpus.Length + index));
            }
        }

        LongRestDiscoveryGeneration second = await h.CaptureAsync(campaign, otherPartition, protectedTarget ? authority : null);

        Assert.NotEqual(Guid.Empty, second.GenerationId);

        // The flood really is in the index the target reads, under another generation.
        string targetTable = protectedTarget ? "long_rest_protected_fts" : "long_rest_clean_fts";

        Assert.True((long)(await h.ScalarAsync($"SELECT count(*) FROM {targetTable} WHERE GenerationId = '{second.GenerationId.ToString("D").ToUpperInvariant()}';"))! > 0);
    }

    [SkippableFact]
    public async Task Weak_shared_stopword_hits_do_not_form_a_group()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int index = 0; index < 41; index++)
        {
            string memory = $"m-{index:D2}";

            await h.SeedAsync(memory, index == 0 ? "the alpha0 only" : $"the beta{index} unique{index}");

            await h.SetEmbeddingAsync(memory, OneHot(41, index));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        // 'the' is in every one of the 41 memories, so it weighs nothing and the 40 hits it makes are not evidence.
        Assert.Empty(LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("m-00"), null, CancellationToken.None)));
    }

    [SkippableFact]
    public async Task A_rare_shared_token_among_common_ones_still_forms_a_group()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int index = 0; index < 41; index++)
        {
            string memory = $"m-{index:D2}";

            await h.SeedAsync(memory, index switch { 0 => "the alpha0 zircon", 7 => "the beta7 zircon", _ => $"the beta{index} unique{index}" });

            await h.SetEmbeddingAsync(memory, OneHot(41, index));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("m-00"), null, CancellationToken.None));

        LongRestCandidate candidate = Assert.Single(Assert.Single(groups).Evidence);

        Assert.Equal("m-07", candidate.Target.MemoryId);

        Assert.Equal(LongRestDiscoveryLane.Lexical, candidate.Lane);

        Assert.True(candidate.Score <= -LongRestDiscoveryLimits.LexicalMinimumScore);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Summary_nodes_are_not_indexed_for_text_search(bool protectedPartition)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        const int leaves = 20;

        GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

        CovenantDigest? partition = null;

        for (int index = 0; index < leaves; index++)
        {
            string id = protectedPartition ? Id(index) : $"m-{index:D2}";

            await h.SeedAsync(id, $"observation number{index} kind{index % 3}");

            await h.SetEmbeddingAsync(id, OneHot(leaves, index));

            if (protectedPartition) partition = await h.ProtectAsync(Guid.Parse(id), provenance);
        }

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        LongRestDiscoveryGeneration generation = await h.CaptureAsync(partition: partition, authority: protectedPartition ? authority : null);

        await h.BuildAsync(generation);

        Assert.True((long)(await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 0;"))! >= 2);

        // Every summary node exists, embedded and published, and has no text-search row: the index holds the captured leaves alone.
        string used = protectedPartition ? "long_rest_protected_fts" : "long_rest_clean_fts";

        string unused = protectedPartition ? "long_rest_clean_fts" : "long_rest_protected_fts";

        Assert.Equal((long)leaves, await h.ScalarAsync($"SELECT count(*) FROM {used};"));

        Assert.Equal(0L, await h.ScalarAsync($"SELECT count(*) FROM {unused};"));

        Assert.Equal(0L, await h.ScalarAsync($"SELECT count(*) FROM {used} fts JOIN long_rest_discovery_nodes node ON node.NodeId = fts.NodeId AND node.GenerationId = fts.GenerationId WHERE node.Layer > 0;"));

        // The summary text the harness accepts is searchable nowhere.
        Assert.Equal(0L, await h.ScalarAsync($"SELECT count(*) FROM {used} WHERE {used} MATCH 'tainttoken';"));
    }

    [SkippableTheory]
    [InlineData(60)]
    [InlineData(200)]
    public async Task Lexical_verification_is_lazy_in_score_order_and_independent_of_the_number_of_hits(int hits)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        // 400 memories; the next 60 or 200 (m-001 onward) share one rare-enough token with the anchor, so every one of them is a hit
        // that clears the floor and all of them score alike, which makes the order memory id. The first five are pinned: ineligible.
        // No two texts are equal, so no hit is an exact duplicate of the anchor.
        for (int index = 0; index < 400; index++)
        {
            string memory = $"m-{index:D3}";

            await h.SeedAsync(memory, index <= hits ? $"zircon w{index:D3}" : $"unique{index}");

            await h.SetEmbeddingAsync(memory, OneHot(400, index));
        }

        await h.ExecuteAsync("UPDATE saga_memories SET PinnedAtUtc = '2026-10-10T00:00:00.0000000Z' WHERE Id IN ('m-001', 'm-002', 'm-003', 'm-004', 'm-005');");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        using AsyncFlowMetricCapture capture = new(LexicalMetric);

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("m-000"), null, CancellationToken.None));

        // An ineligible hit that outranks the rest costs one verification and no slot; then eight eligible neighbours fill the lane
        // and verification stops, with about 190 more hits unread.
        Assert.Equal(["m-006", "m-007", "m-008", "m-009", "m-010", "m-011", "m-012", "m-013"],
            groups.SelectMany(static group => group.Evidence).Where(static candidate => candidate.Lane == LongRestDiscoveryLane.Lexical).Select(static candidate => candidate.Target.MemoryId));

        // Whatever ineligible memories the generation captured at all are the prefix verified before the eight that fill the lane.
        long capturedIneligible = (long)(await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_leaves WHERE MemoryId IN ('m-001', 'm-002', 'm-003', 'm-004', 'm-005');"))!;

        Assert.Equal(capturedIneligible + 8, capture.Sum(LexicalMetric));
    }

    [SkippableFact]
    public async Task Lexical_ties_break_by_memory_id_whatever_the_insertion_order()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("anchor", "opal quartz");

        foreach (string memory in new[] { "z-token", "m-token", "a-token", "q-token" })
        {
            await h.SeedAsync(memory, "opal opal gem");
        }

        for (int index = 0; index < 20; index++)
        {
            await h.SeedAsync($"b-{index:D2}", $"unique{index} other{index}");
        }

        string[] all = ["anchor", "z-token", "m-token", "a-token", "q-token", .. Enumerable.Range(0, 20).Select(static index => $"b-{index:D2}")];

        for (int index = 0; index < all.Length; index++)
        {
            await h.SetEmbeddingAsync(all[index], OneHot(all.Length, index));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET PhaseCode = 4, Published = 1;");

        LongRestCandidate[] lexical = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("anchor"), null, CancellationToken.None))
            .SelectMany(static group => group.Evidence).Where(static candidate => candidate.Lane == LongRestDiscoveryLane.Lexical).ToArray();

        Assert.Equal(["a-token", "m-token", "q-token", "z-token"], lexical.Select(static candidate => candidate.Target.MemoryId));

        Assert.Single(lexical.Select(static candidate => candidate.Score).Distinct());

        Assert.All(lexical, static candidate => Assert.True(candidate.Score < 0));
    }
}
