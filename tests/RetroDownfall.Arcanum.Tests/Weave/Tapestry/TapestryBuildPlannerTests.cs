using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

public sealed class TapestryBuildPlannerTests
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/raptor");

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void SmallLayer_TerminatesAtEleven(int count)
    {
        var nodes = Nodes(count);

        var clusterer = new Splitter((points, call) => throw new InvalidOperationException("No clustering on small roots."));

        var result = new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, new Summary(), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(TapestryPlanTerminalReason.SmallLayer, result.Value.TerminalReason);

        Assert.Empty(result.Value.Summaries);

        Assert.Equal(nodes.Select(n => n.StableId), result.Value.Carries.Select(n => n.StableId));
    }

    [Fact]
    public void RecursiveSplit_StopsOnRepeatedMemberSet_AndRefinesActualFit()
    {
        var nodes = Nodes(30);

        var clusterer = new Splitter((points, call) => Groups(points, [Enumerable.Range(0, points.Count).ToArray()]));

        var result = new TapestryBuildPlanner(24).Plan(Scope, nodes, 1, clusterer, new Summary(3), default);

        Assert.True(result.IsSuccess);

        Assert.InRange(clusterer.Calls, 1, 2);

        Assert.Null(result.Value.TerminalReason);

        Assert.All(result.Value.Summaries, c => Assert.InRange(c.Children.Length, 2, 3));

        Assert.All(result.Value.Summaries, c => Assert.True(c.Reason.HasFlag(TapestryPlanReason.NonContractingPartition)));

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void AllSources_RemainInClustersOrCarries_IncludingOversizedSingleton()
    {
        var nodes = Nodes(13);

        nodes[0] = nodes[0] with { Content = new string('x', 100) };

        var clusterer = new Splitter((points, call) => Groups(points, [Enumerable.Range(0, points.Count).ToArray()]));

        var result = new TapestryBuildPlanner(4).Plan(Scope, nodes, 1, clusterer, new Summary(4, 20), default);

        Assert.True(result.IsSuccess);

        AssertCoverage(nodes, result.Value);

        Assert.Contains(result.Value.Carries, c => c.StableId == "p000" && c.Reason.HasFlag(TapestryPlanReason.OversizedCarry));

        Assert.All(result.Value.Summaries, c => Assert.InRange(c.Children.Length, 2, 4));
    }

    [Fact]
    public void NoncontractingFrontier_DoesNotSpend_AndKeepsOriginalRoots()
    {
        var nodes = Nodes(12);

        var clusterer = new Splitter((points, call) => Groups(points, Enumerable.Range(0, points.Count).Select(i => new[] { i, (i + 1) % points.Count }).ToArray()));

        var summary = new Summary();

        var result = new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, summary, default);

        Assert.True(result.IsSuccess);

        Assert.Equal(TapestryPlanTerminalReason.NonContracting, result.Value.TerminalReason);

        Assert.Empty(result.Value.Summaries);

        Assert.Equal(12, result.Value.Carries.Length);

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void MissingOrForeignSourceCoverage_IsRefused()
    {
        var nodes = Nodes(12);

        var clusterer = new Splitter((points, call) => Groups(points, [new[] { 0, 1 }]));

        var result = new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, new Summary(), default);

        Assert.True(result.IsFailure);

        Assert.Equal("Tapestry.InvalidClusterCoverage", result.Error.Code);
    }

    [Fact]
    public void TwelveNodesCanContract_AndInputPermutationKeepsPlanIdentity()
    {
        var nodes = Nodes(12);

        var clusterer = new Splitter((points, call) => Groups(points, [Enumerable.Range(0, 6).ToArray(), Enumerable.Range(6, 6).ToArray()]));

        var planner = new TapestryBuildPlanner();

        var first = planner.Plan(Scope, nodes, 1, clusterer, new Summary(), default);

        var second = planner.Plan(Scope, nodes.Reverse().ToArray(), 1, clusterer, new Summary(), default);

        Assert.True(first.IsSuccess && second.IsSuccess);

        Assert.Null(first.Value.TerminalReason);

        Assert.Equal(2, first.Value.Summaries.Length);

        Assert.Equal(first.Value.Summaries.Select(c => c.StableId), second.Value.Summaries.Select(c => c.StableId));

        AssertCoverage(nodes, first.Value);
    }

    [Fact]
    public void DistinctOverlappingSubsets_ExhaustBudgetAsIncompleteWithoutSpendOrLostRoots()
    {
        var nodes = Nodes(100);

        var clusterer = new Splitter((points, call) => Groups(points, Enumerable.Range(0, 80).Select(skip => Enumerable.Range(0, points.Count).Where(i => i != skip).ToArray()).ToArray()));

        var result = new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, new Summary(), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(TapestryPlanTerminalReason.WorkLimit, result.Value.TerminalReason);

        Assert.Equal(TapestryPlanDisposition.Incomplete, result.Value.Disposition);

        Assert.Empty(result.Value.Summaries);

        Assert.Equal(1, clusterer.Calls);

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void OneFittingCluster_PreservesSemanticReasonWithoutPartition()
    {
        var nodes = Nodes(12);

        var clusterer = new Splitter((points, call) => Groups(points, [Enumerable.Range(0, points.Count).ToArray()]));

        var result = new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, new Summary(), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(TapestryPlanReason.None, Assert.Single(result.Value.Summaries).Reason);

        Assert.Equal(12, result.Value.Summaries[0].Children.Length);
    }

    [Fact]
    public void OversizedProperSubsets_AreReallyReclustered_WithAllStageEvidence()
    {
        var nodes = Nodes(60);

        var clusterer = new Splitter((points, call) => Groups(points, points.Count == 60
            ? [Enumerable.Range(0, 30).ToArray(), Enumerable.Range(30, 30).ToArray()]
            : Enumerable.Range(0, 10).Select(i => Enumerable.Range(i * 3, 3).ToArray()).ToArray()));

        var result = new TapestryBuildPlanner(3).Plan(Scope, nodes, 1, clusterer, new Summary(3), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(3, clusterer.Calls);

        Assert.Equal(3, result.Value.Evidence.Length);

        Assert.Equal(20, result.Value.Summaries.Length);

        Assert.All(result.Value.Summaries, c => Assert.Equal(3, c.Children.Length));

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void DuplicateGroups_CoalesceAndRetainEveryPath()
    {
        var nodes = Nodes(12);

        var clusterer = new Splitter((points, call) => Groups(points, [Enumerable.Range(0, 6).ToArray(), Enumerable.Range(0, 6).ToArray(), Enumerable.Range(6, 6).ToArray()]));

        var result = new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, new Summary(), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, result.Value.Summaries.Length);

        var duplicate = Assert.Single(result.Value.Summaries, c => c.Reason.HasFlag(TapestryPlanReason.DuplicatePaths));

        Assert.All(duplicate.Children, c => Assert.Equal(2, c.Paths.Length));

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void CancellationDuringRequestFit_StopsPlanning()
    {
        var nodes = Nodes(12);

        using var cts = new CancellationTokenSource();

        var clusterer = new Splitter((points, call) => Groups(points, [Enumerable.Range(0, points.Count).ToArray()]));

        Assert.Throws<OperationCanceledException>(() => new TapestryBuildPlanner().Plan(Scope, nodes, 1, clusterer, new CancellingSummary(cts), cts.Token));
    }

    private sealed class CancellingSummary(CancellationTokenSource cts) : ITapestrySummarizer
    {
        public string? ResolveSummaryModel() => "test";

        public bool FitsOneRequest(TapestrySummaryRequest request) { cts.Cancel(); return true; }
        public Task<Result<string>> SummarizeAsync(TapestrySummaryRequest request, CancellationToken token) => throw new InvalidOperationException("No spend.");
    }

    [Fact]
    public void SharedOversizedMemberSet_IsClusteredAtMostOnce()
    {
        var nodes = Nodes(60);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        var clusterer = new Splitter((points, call) =>
        {
            Assert.True(seen.Add(string.Join(",", points.Select(p => p.StableId))));

            return Groups(points, points.Count switch
            {
                60 => [Enumerable.Range(0, 40).ToArray(), Enumerable.Range(20, 40).ToArray()],
                40 => [Enumerable.Range(0, 20).ToArray(), Enumerable.Range(20, 20).ToArray()],
                _ => Enumerable.Range(0, points.Count / 2).Select(i => new[] { i * 2, i * 2 + 1 }).ToArray(),
            });
        });

        var result = new TapestryBuildPlanner(3).Plan(Scope, nodes, 1, clusterer, new Summary(3), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(6, clusterer.Calls);

        Assert.Null(result.Value.TerminalReason);

        Assert.Contains(result.Value.Summaries, c => c.Reason.HasFlag(TapestryPlanReason.NonContractingPartition));

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void FullSizeOversizedCluster_PreservesFittingOverlappingSemanticSibling()
    {
        var nodes = Nodes(12).Select((node, index) => node with { Embedding = new float[] { 1, index + 1, -index } }).ToArray();

        var clusterer = new Splitter((points, call) => FullWithSibling(points.Count, [0, 4, 8]));

        var result = new TapestryBuildPlanner(4).Plan(Scope, nodes, 1, clusterer, new Summary(4), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(TapestryPlanDisposition.Ready, result.Value.Disposition);

        Assert.Null(result.Value.TerminalReason);

        Assert.Equal(new[]
        {
            "p000,p001,p002,p003",
            "p000,p004,p008",
            "p004,p005,p006,p007",
            "p008,p009,p010,p011",
        }, result.Value.Summaries.Select(c => string.Join(",", c.Children.Select(child => child.StableId))).Order(StringComparer.Ordinal));

        var semantic = Assert.Single(result.Value.Summaries, c => c.Children.Select(child => child.StableId).SequenceEqual(new[] { "p000", "p004", "p008" }));

        Assert.Equal(TapestryPlanReason.None, semantic.Reason);

        Assert.All(semantic.Children, child =>
        {
            var path = Assert.Single(child.Paths);

            Assert.Equal("semantic", path.ClusterId);

            Assert.Equal(.4, path.Membership.GlobalPosterior);

            Assert.Equal(1, path.Membership.LocalPosterior);
        });

        Assert.Equal(1, clusterer.Calls);

        AssertCoverage(nodes, result.Value);
    }

    [Fact]
    public void FullSizeOversizedCluster_ReclustersProperOversizedSemanticSibling()
    {
        var nodes = Nodes(24).Select((node, index) => node with { Embedding = new float[] { 1, index + 1, -index } }).ToArray();

        var clusterer = new Splitter((points, call) =>
        {
            if (call == 1) return FullWithSibling(points.Count, [0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22]);

            Assert.Equal(new[] { "p000", "p002", "p004", "p006", "p008", "p010", "p012", "p014", "p016", "p018", "p020", "p022" }, points.Select(point => point.StableId));

            return Groups(points, [[0, 3, 6, 9], [1, 4, 7, 10], [2, 5, 8, 11]]);
        });

        var result = new TapestryBuildPlanner(4).Plan(Scope, nodes, 1, clusterer, new Summary(4), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, clusterer.Calls);

        Assert.Equal(TapestryPlanDisposition.Ready, result.Value.Disposition);

        Assert.Null(result.Value.TerminalReason);

        Assert.Equal(new[]
        {
            "p000,p001,p002,p003",
            "p000,p006,p012,p018",
            "p002,p008,p014,p020",
            "p004,p005,p006,p007",
            "p004,p010,p016,p022",
            "p008,p009,p010,p011",
            "p012,p013,p014,p015",
            "p016,p017,p018,p019",
            "p020,p021,p022,p023",
        }, result.Value.Summaries.Select(c => string.Join(",", c.Children.Select(child => child.StableId))).Order(StringComparer.Ordinal));

        Assert.Equal(2, result.Value.Evidence.Length);

        Assert.Equal(3, result.Value.Summaries.Count(c => c.Reason == TapestryPlanReason.None));

        Assert.All(result.Value.Evidence[0].Result.Clusters.Single(c => c.StableId == "semantic").Members, member => Assert.Equal(.4, member.GlobalPosterior));

        AssertCoverage(nodes, result.Value);
    }

    private static RaptorClusterResult FullWithSibling(int count, int[] sibling) => new(
    [
        new("full", Enumerable.Range(0, count).Select(i => new RaptorMembership(i, 0, 0, sibling.Contains(i) ? .6 : 1, 1, RaptorRepairReason.None)).ToImmutableArray()),
        new("semantic", sibling.Select(i => new RaptorMembership(i, 1, 0, .4, 1, RaptorRepairReason.None)).ToImmutableArray()),
    ], []);

    private static void AssertCoverage(TapestryWorkingNode[] nodes, TapestryLayerPlan plan) => Assert.Equal(nodes.Select(n => n.StableId).Order(), plan.Summaries.SelectMany(c => c.Children).Select(c => c.StableId).Concat(plan.Carries.Select(c => c.StableId)).Distinct().Order());

    private static TapestryWorkingNode[] Nodes(int count) => Enumerable.Range(0, count).Select(i => new TapestryWorkingNode($"p{i:D3}", "text", new float[] { 1, 2, 3 })).ToArray();

    private static RaptorClusterResult Groups(IReadOnlyList<RaptorPoint> points, int[][] groups) => new(groups.Select((g, i) => new RaptorCluster($"group{i}", g.Select(p => new RaptorMembership(p, i, 0, 1, 1, RaptorRepairReason.None)).ToImmutableArray())).ToImmutableArray(), []);

    private sealed class Splitter(Func<IReadOnlyList<RaptorPoint>, int, RaptorClusterResult> split) : ITapestryClusterer
    {
        public int Calls { get; private set; }
        public Result<RaptorClusterResult> Cluster(IReadOnlyList<RaptorPoint> points, TapestryClusterIdentity identity, CancellationToken token) => split(points, ++Calls);
    }

    private sealed class Summary(int maxCount = 24, int maxLength = 10000) : ITapestrySummarizer
    {
        public string? ResolveSummaryModel() => "test";

        public bool FitsOneRequest(TapestrySummaryRequest request) => request.ChildTexts.Count <= maxCount && request.ChildTexts.Sum(t => t.Length) <= maxLength;

        public Task<Result<string>> SummarizeAsync(TapestrySummaryRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("Planning must never spend.");
    }
}
