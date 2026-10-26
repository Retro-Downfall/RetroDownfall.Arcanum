using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorClustererTests
{
    private static readonly TapestryScope Scope = new(TapestryScopeKind.Workspace, "/raptor");

    [Fact]
    public void LocalReduction_UsesOriginalEmbeddingSubset()
    {
        var points = Points(24);

        int localCalls = 0;

        RaptorReduce reduce = (input, neighbors, dimensions, identity, token) =>
        {
            Assert.Equal(10, dimensions);

            Assert.Equal(identity.Stage == RaptorStage.GlobalUmap ? 4 : 10, neighbors);

            if (identity.Stage == RaptorStage.LocalUmap)
            {
                localCalls++;

                Assert.Equal(12, input.Count);

                Assert.All(input, p => Assert.Equal(points.Single(x => x.StableId == p.StableId).Vector.ToArray(), p.Vector.ToArray()));
            }

            return input.Select(p => new[] { (double)int.Parse(p.StableId[1..]), -100.0 }).ToArray();
        };

        RaptorFit fit = (rows, seed, token) => new Rows(rows.Select(row => rows.Length == 24 ? (row[0] < 12 ? new[] { 1.0, 0.0 } : new[] { 0.0, 1.0 }) : new[] { 1.0 }).ToArray());

        var result = new RaptorClusterer(reduce, fit).Cluster(points, Identity(points), default);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : "");

        Assert.Equal(2, localCalls);

        Assert.Equal(2, result.Value.Clusters.Length);
    }

    [Fact]
    public void BridgePoint_BelongsToTwoSummaries_AndDuplicateVectorsMapByIndex()
    {
        var points = Points(12);

        var rows = Enumerable.Range(0, 12).Select(i => i == 0 ? new[] { .55, .45 } : i < 6 ? new[] { 1.0, 0.0 } : new[] { 0.0, 1.0 }).ToArray();

        var result = Fake(rows).Cluster(points, Identity(points), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(2, result.Value.Clusters.Length);

        Assert.Equal(2, result.Value.Clusters.SelectMany(c => c.Members).Count(m => m.PointIndex == 0));

        Assert.Equal(Enumerable.Range(0, 12), result.Value.Clusters.SelectMany(c => c.Members).Select(m => m.PointIndex).Distinct().Order());

        Assert.Contains(result.Value.Clusters.SelectMany(c => c.Members), m => m.PointIndex == 0 && m.GlobalPosterior == .45 && m.LocalPosterior == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Threshold_IsStrictlyGreaterThanPointOne_AtBothStages(bool local)
    {
        var points = Points(12);

        var boundary = Enumerable.Range(0, 12).Select(_ => new[] { .1, .9 }).ToArray();

        int calls = 0;

        var clusterer = new RaptorClusterer(Reduce, (rows, seed, token) => new Rows(local && calls++ == 0 ? rows.Select(_ => new[] { 1.0 }).ToArray() : boundary));

        var result = clusterer.Cluster(points, Identity(points), default);

        Assert.True(result.IsSuccess);

        var members = Assert.Single(result.Value.Clusters).Members;

        Assert.Equal(12, members.Length);

        Assert.All(members, m => Assert.Equal(.9, local ? m.LocalPosterior : m.GlobalPosterior));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoMembership_RepairsWithoutDroppingSource(bool local)
    {
        var points = Points(12);

        int calls = 0;

        var clusterer = new RaptorClusterer(Reduce, (rows, seed, token) => new Rows(local && calls++ == 0 ? rows.Select(_ => new[] { 1.0 }).ToArray() : rows.Select(_ => Enumerable.Repeat(.1, 10).ToArray()).ToArray()));

        var result = clusterer.Cluster(points, Identity(points), default);

        Assert.True(result.IsSuccess);

        Assert.All(Assert.Single(result.Value.Clusters).Members, m => Assert.True(m.Repair.HasFlag(local ? RaptorRepairReason.LocalNoMembership : RaptorRepairReason.GlobalNoMembership)));

        Assert.Equal(12, result.Value.Clusters[0].Members.Length);
    }

    [Fact]
    public void SameMemberSets_CoalesceWithoutLosingPosteriorPaths()
    {
        var points = Points(12);

        var result = Fake(points.Select(_ => new[] { .6, .4 }).ToArray()).Cluster(points, Identity(points), default);

        Assert.True(result.IsSuccess);

        var cluster = Assert.Single(result.Value.Clusters);

        Assert.Equal(48, cluster.Members.Length);

        Assert.All(cluster.Members, m => Assert.True(m.Repair.HasFlag(RaptorRepairReason.DuplicatePaths)));

        Assert.Equal(12, cluster.Members.Select(m => m.PointIndex).Distinct().Count());
    }

    [Fact]
    public void CancellationAndInvalidVectors_RefuseEvenSmallInputs()
    {
        var points = Points(2);

        using var cts = new CancellationTokenSource();

        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new RaptorClusterer().Cluster(points, Identity(points), cts.Token));

        points[0] = points[0] with { Vector = new float[] { float.NaN } };

        Assert.True(new RaptorClusterer().Cluster(points, Identity(points), default).IsFailure);
    }

    [Fact]
    public void EveryQualifyingMembership_IsRetainedAtBothStages()
    {
        var points = Points(12);

        var result = Fake(points.Select(_ => Enumerable.Repeat(1.0 / 9, 9).ToArray()).ToArray()).Cluster(points, Identity(points), default);

        Assert.True(result.IsSuccess);

        Assert.Equal(12 * 81, Assert.Single(result.Value.Clusters).Members.Length);
    }

    [Theory]
    [InlineData("identical")]
    [InlineData("near-identical")]
    [InlineData("separated-overlap")]
    public void ActualKernels_PreserveCoverageAndSameRuntimePermutationIdentity(string kind)
    {
        var points = Enumerable.Range(0, 24).Select(i => new RaptorPoint($"real{i:D3}", kind switch
        {
            "identical" => new float[] { 1, 2, 3, 4 },
            "near-identical" => new float[] { 1, 2 + i * 1e-6f, 3, 4 },
            _ => new float[] { 1, (i < 11 ? -2 : i > 12 ? 2 : 0) + (float)Math.Sin(i) * .1f, (float)Math.Cos(i), i * .01f },
        })).ToArray();

        var clusterer = new RaptorClusterer();

        var first = clusterer.Cluster(points, Identity(points), default);

        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.Code : "");

        var permuted = points.Reverse().ToArray();

        var second = clusterer.Cluster(permuted, Identity(permuted), default);

        Assert.True(second.IsSuccess, second.IsFailure ? second.Error.Code : "");

        Assert.Equal(Enumerable.Range(0, points.Length), first.Value.Clusters.SelectMany(c => c.Members).Select(m => m.PointIndex).Distinct().Order());

        Assert.Equal(first.Value.Clusters.Select(c => c.StableId), second.Value.Clusters.Select(c => c.StableId));

        Assert.Equal(Signature(first.Value, points), Signature(second.Value, permuted));

        Assert.All(first.Value.Clusters.SelectMany(c => c.Members), m =>
        {
            Assert.InRange(m.GlobalPosterior, 0, 1);

            Assert.InRange(m.LocalPosterior, 0, 1);
        });
    }

    private static string[] Signature(RaptorClusterResult result, RaptorPoint[] points) => result.Clusters.SelectMany(c => c.Members.Select(m => $"{c.StableId}:{points[m.PointIndex].StableId}:{m.GlobalComponent}:{m.LocalComponent}:{m.GlobalPosterior:R}:{m.LocalPosterior:R}:{m.Repair}")).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void OptionalNumericObservers_DoNotChangeGraphOrOptimizedCoordinates()
    {
        var points = Enumerable.Range(0, 50).Select(i => new RaptorPoint($"observe{i:D3}", new float[] { 1, (float)Math.Sin(i), (float)Math.Cos(i) })).ToArray();

        var neighborObserver = new Observer<RaptorNeighborProgress>();

        var plain = new RaptorNeighborGraphBuilder().Build(points, 5, 71, default).Value;

        var observed = new RaptorNeighborGraphBuilder(neighborObserver).Build(points, 5, 71, default).Value;

        Assert.NotEmpty(neighborObserver.Values);

        Assert.Equal(plain.RowOffsets.ToArray(), observed.RowOffsets.ToArray());

        Assert.Equal(plain.SourceIndices.ToArray(), observed.SourceIndices.ToArray());

        Assert.Equal(plain.Distances.ToArray(), observed.Distances.ToArray());

        Assert.Equal(plain.DistanceEvaluations, observed.DistanceEvaluations);

        var fuzzy = new RaptorFuzzyGraphBuilder().Build(plain, default).Value;

        var optimizerObserver = new Observer<RaptorUmapProgress>();

        var plainLayout = new RaptorUmapOptimizer().Optimize(fuzzy, 2, 71, default).Value;

        var observedLayout = new RaptorUmapOptimizer(optimizerObserver).Optimize(fuzzy, 2, 71, default).Value;

        Assert.NotEmpty(optimizerObserver.Values);

        Assert.Equal(plainLayout.Select(p => p.SourceIndex), observedLayout.Select(p => p.SourceIndex));

        Assert.Equal(plainLayout.SelectMany(p => p.Coordinates), observedLayout.SelectMany(p => p.Coordinates));

        double[][] rows = Enumerable.Range(0, 30).Select(i => new double[] { Math.Sin(i), Math.Cos(i) }).ToArray();

        var mixtureProgress = new Observer<RaptorGmmProgress>();

        var mixtureDiagnostics = new Observer<RaptorGmmCandidate>();

        var plainFit = new RaptorGaussianMixture().SelectByBic(rows, 71, default).Value;

        var observedFit = new RaptorGaussianMixture(mixtureProgress, mixtureDiagnostics).SelectByBic(rows, 71, default).Value;

        Assert.NotEmpty(mixtureProgress.Values);

        Assert.NotEmpty(mixtureDiagnostics.Values);

        Assert.Equal(plainFit.Weights.ToArray(), observedFit.Weights.ToArray());

        Assert.Equal(plainFit.Means.SelectMany(m => m), observedFit.Means.SelectMany(m => m));

        Assert.Equal(plainFit.Covariances.SelectMany(c => c).SelectMany(row => row), observedFit.Covariances.SelectMany(c => c).SelectMany(row => row));

        Assert.Equal(plainFit.Bic, observedFit.Bic);

        Assert.Equal(plainFit.TotalLogLikelihood, observedFit.TotalLogLikelihood);

        Assert.Equal(plainFit.Iterations, observedFit.Iterations);
    }

    private sealed class Observer<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    internal static RaptorPoint[] Points(int count) => Enumerable.Range(0, count).Select(i => new RaptorPoint($"p{i:D3}", new float[] { 1, 2, 3 })).ToArray();

    private static TapestryClusterIdentity Identity(RaptorPoint[] points) => new(Scope, 1, RaptorStage.GlobalUmap, "", points.Select(p => p.StableId));

    private static Result<double[][]> Reduce(IReadOnlyList<RaptorPoint> input, int neighbors, int dimensions, TapestryClusterIdentity identity, CancellationToken token) => input.Select(_ => new double[dimensions]).ToArray();

    private static RaptorClusterer Fake(double[][] rows) => new(Reduce, (input, seed, token) => new Rows(rows));

    private sealed class Rows(double[][] rows) : RaptorPosteriorModel
    {
        public override int Components => rows[0].Length;

        public override Result<double> Posterior(int index, Span<double> destination) { rows[index].CopyTo(destination); return 0.0; }
    }
}
