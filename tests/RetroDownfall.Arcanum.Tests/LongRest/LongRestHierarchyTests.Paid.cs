using System.Diagnostics.Metrics;

using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestHierarchyTests
{
    [SkippableTheory]
    [InlineData("embedding-paid")]
    [InlineData("accepted-stage")]
    [InlineData("child-edge")]
    [InlineData("node-revision")]
    [InlineData("node-partition")]
    public async Task Generated_queries_require_original_paid_stage_and_exact_child_proof(string corruption)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("a", "amber first");

        await h.SeedAsync("b", "ochre second");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        await h.BuildAsync(generation);

        await h.ExecuteAsync(corruption switch
        {
            "embedding-paid" => "UPDATE long_rest_discovery_embeddings SET PaidEvidenceJson = '{}';",

            "accepted-stage" => "DELETE FROM long_rest_accepted_summaries;",

            "node-revision" => "UPDATE long_rest_discovery_nodes SET Revision = 1 WHERE Layer > 0;",

            "node-partition" => "UPDATE long_rest_discovery_nodes SET SensitivityCode = 1, PartitionDigest = zeroblob(32), ProvenanceJson = '{}' WHERE Layer > 0;",

            _ => "DELETE FROM long_rest_discovery_edges WHERE Ordinal = 2;",
        });

        Assert.True((await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("a"), null, CancellationToken.None)).IsFailure);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_summary_survives_a_real_boot_restart_for_embedding_only(bool protectedPartition)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        string a = Guid.NewGuid().ToString("D");

        string b = Guid.NewGuid().ToString("D");

        await h.SeedAsync(a, "first observation");

        await h.SeedAsync(b, "second observation");

        CovenantDigest? partition = null;

        if (protectedPartition)
        {
            GenerationProvenance provenance = GenerationProvenance.CreateExact([Guid.NewGuid()]);

            partition = await h.ProtectAsync(Guid.Parse(a), provenance);

            await h.ProtectAsync(Guid.Parse(b), provenance);
        }

        await using CovenantTurnLease authority = (await CovenantOperationGateFixture.CreateGate().AcquireTurnAsync(CanonicalCampaignContext.GlobalOnly, CancellationToken.None)).Value;

        LongRestDiscoveryGeneration generation = await h.CaptureAsync(partition: partition, authority: protectedPartition ? authority : null);

        LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, h.Capability, CancellationToken.None))!;

        LongRestAcceptedHierarchySummary accepted = await h.SummaryAsync(work);

        Assert.Equal(0L, await h.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));

        string original = (string)(await h.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"))!;

        // A failed paid embedding is durable, then a genuinely new boot re-admits current work.
        await h.EmbeddingAsync(work, accepted);

        await h.ExecuteAsync("UPDATE long_rest_provider_effects SET StateCode = 6;");

        await using var context = h.Saga.CreateSiblingContext();

        LongRestDiscoveryStore restarted = new(context, TimeProvider.System);

        LongRestSweepFence fence = generation.Fence with { Configuration = generation.Fence.Configuration with { BootId = Guid.NewGuid(), Epoch = 4 } };

        LongRestSweepHarness.Require(await restarted.StartOrResumeAsync(generation.OperationId, generation.Scope, fence, generation.Recipe, CancellationToken.None));

        LongRestMaintenanceCapability? currentCapability = protectedPartition
            ? await LongRestSweepHarness.CreateCapabilityAsync(generation with { Fence = fence }, authority)
            : null;

        if (currentCapability is not null)
        {
            Assert.True((await currentCapability.ValidateWorkAsync(work.Fold.Work, fence.Configuration, CancellationToken.None)).IsFailure);
        }

        LongRestHierarchyWork next = LongRestSweepHarness.Require(await restarted.ReadNextFoldAsync(generation.GenerationId, currentCapability, CancellationToken.None))!;

        Assert.NotNull(next.AcceptedSummary);

        Assert.NotEqual(LongRestDiscoveryHash.Work(work.Fold.Work).Value, LongRestDiscoveryHash.Work(next.Fold.Work).Value);

        Assert.Equal(LongRestDiscoveryHash.Work(work.Fold.Work).Value, LongRestDiscoveryHash.Work(next.AcceptedSummary.Fold.Work).Value);

        Assert.Equal(original, await h.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));

        LongRestHierarchyPublication publication = await h.EmbeddingAsync(next, next.AcceptedSummary, ordinal: 3);

        LongRestSweepHarness.Require(await restarted.CommitFoldAsync(publication, currentCapability, CancellationToken.None));

        Assert.Equal(1, h.SummarySends);

        Assert.Equal(2, h.EmbeddingSends);

        if (protectedPartition)
        {
            Assert.NotEqual(accepted.PaidEvidence.Attempt.DisclosureReceiptDigest, publication.EmbeddingPaidEvidence.Attempt.DisclosureReceiptDigest);
        }

        Assert.Equal(3L, await h.ScalarAsync("SELECT PhysicalOrdinal FROM long_rest_provider_effects;"));

        Assert.Equal(original, await h.ScalarAsync("SELECT EvidenceJson FROM long_rest_accepted_summaries;"));
    }

    [SkippableTheory]
    [InlineData("wrong-text")]
    [InlineData("missing-completion")]
    [InlineData("wrong-usage")]
    public async Task Publication_requires_exact_paid_input_and_current_completion(string fault)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("a", "first observation");

        await h.SeedAsync("b", "second observation");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None))!;

        LongRestAcceptedHierarchySummary accepted = await h.SummaryAsync(work);

        LongRestHierarchyPublication publication = await h.EmbeddingAsync(work, accepted, fault == "wrong-text" ? "different paid text" : null);

        if (fault == "missing-completion")
        {
            await h.ExecuteAsync("UPDATE long_rest_provider_effects SET CompletionJson = NULL;");
        }
        else if (fault == "wrong-usage")
        {
            publication = publication with { EmbeddingPaidEvidence = publication.EmbeddingPaidEvidence with { InputTokens = 9 } };
        }

        Assert.True((await h.Store.CommitFoldAsync(publication, null, CancellationToken.None)).IsFailure);

        Assert.Equal(2L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));
    }

    [SkippableFact]
    public async Task Fold_fault_rolls_back_node_vector_edges_and_cursor_then_replays_once()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("a", "first observation");

        await h.SeedAsync("b", "second observation");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestHierarchyWork work = LongRestSweepHarness.Require(await h.Store.ReadNextFoldAsync(generation.GenerationId, null, CancellationToken.None))!;

        LongRestAcceptedHierarchySummary accepted = await h.SummaryAsync(work);

        LongRestHierarchyPublication publication = await h.EmbeddingAsync(work, accepted);

        await h.ExecuteAsync("CREATE TRIGGER fail_fold_cursor BEFORE UPDATE OF FoldGroup ON long_rest_discovery_generations BEGIN SELECT RAISE(ABORT, 'fold fault'); END;");

        Assert.True((await h.Store.CommitFoldAsync(publication, null, CancellationToken.None)).IsFailure);

        Assert.Equal(2L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_edges;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_embeddings;"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT FoldGroup FROM long_rest_discovery_generations;"));

        await h.ExecuteAsync("DROP TRIGGER fail_fold_cursor;");

        LongRestSweepHarness.Require(await h.Store.CommitFoldAsync(publication, null, CancellationToken.None));

        LongRestSweepHarness.Require(await h.Store.CommitFoldAsync(publication, null, CancellationToken.None));

        Assert.Equal(3L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes;"));

        Assert.Equal(1, h.SummarySends);

        Assert.Equal(1, h.EmbeddingSends);
    }

    [SkippableFact]
    public async Task Hierarchy_only_recall_expands_an_upper_node_across_source_pages()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        for (int index = 0; index < 257; index++)
        {
            string memory = $"m-{index:D3}";

            await h.SeedAsync(memory, $"uniquetoken{index:D3}");

            float[] vector = new float[257];

            vector[index] = 1;

            await h.ExecuteAsync("UPDATE saga_memory_embeddings SET Dim = 257, Embedding = $vector WHERE MemoryId = $memory;",
                ("$vector", EmbeddingBlobCodec.Encode(vector)), ("$memory", memory));
        }

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestDiscoveryGeneration published = await h.BuildAsync(generation);

        Assert.True(published.Published);

        Assert.True((long)(await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer > 1;"))! > 0);

        Assert.Equal(257L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_nodes WHERE Layer = 0;"));

        Assert.Equal(257L, await h.ScalarAsync("""
            WITH RECURSIVE covered(NodeId) AS (
                SELECT NodeId FROM long_rest_discovery_nodes node WHERE NOT EXISTS (
                    SELECT 1 FROM long_rest_discovery_edges edge WHERE edge.ChildNodeId = node.NodeId)
                UNION ALL SELECT edge.ChildNodeId FROM long_rest_discovery_edges edge JOIN covered ON covered.NodeId = edge.ParentNodeId)
            SELECT count(*) FROM (SELECT node.MemoryId FROM covered JOIN long_rest_discovery_nodes node USING(NodeId)
                WHERE node.Layer = 0 GROUP BY node.MemoryId HAVING count(*) = 1);
            """));

        Assert.InRange((long)(await h.ScalarAsync("SELECT max(Layer) FROM long_rest_discovery_nodes;"))!, 2, 6);

        Assert.InRange((long)(await h.ScalarAsync("SELECT max(n) FROM (SELECT count(*) n FROM long_rest_discovery_edges GROUP BY ParentNodeId);"))!, 2, 16);

        Dictionary<string, List<int>> measurements = new(StringComparer.Ordinal)
        {
            ["arcanum.long_rest.discovery.selected_summaries"] = [],

            ["arcanum.long_rest.discovery.frontier"] = [],

            ["arcanum.long_rest.discovery.expanded_leaves"] = [],
        };

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, observed) =>
        {
            if (instrument.Meter.Name == "Arcanum" && measurements.ContainsKey(instrument.Name))
            {
                observed.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
        {
            Assert.Empty(tags.ToArray());

            measurements[instrument.Name].Add(measurement);
        });

        listener.Start();

        LongRestCandidateGroup[] groups = LongRestSweepHarness.Require(await h.Store.DiscoverAsync(generation.GenerationId, await h.TargetAsync("m-000"), null, CancellationToken.None));

        LongRestCandidate[] evidence = groups.SelectMany(group => group.Evidence).ToArray();

        Assert.NotEmpty(evidence);

        Assert.All(evidence, candidate => Assert.Equal(LongRestDiscoveryLane.Hierarchy, candidate.Lane));

        Assert.Contains(evidence, candidate => StringComparer.Ordinal.Compare(candidate.Target.MemoryId, "m-128") >= 0);

        Assert.InRange(evidence.Length, 1, 32);

        Assert.All(groups, group => Assert.InRange(group.Targets.Length, 2, 16));

        Assert.InRange(Assert.Single(measurements["arcanum.long_rest.discovery.selected_summaries"]), 1, 8);

        Assert.NotEmpty(measurements["arcanum.long_rest.discovery.frontier"]);

        Assert.All(measurements["arcanum.long_rest.discovery.frontier"], value => Assert.InRange(value, 0, 128));

        Assert.NotEmpty(measurements["arcanum.long_rest.discovery.expanded_leaves"]);

        Assert.All(measurements["arcanum.long_rest.discovery.expanded_leaves"], value => Assert.InRange(value, 0, 32));
    }
}
