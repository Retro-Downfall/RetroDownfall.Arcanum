using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

using Xunit.Abstractions;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorUmapTests(ITestOutputHelper output)
{
    [Fact]
    public void FixedInitialEpoch_MatchesGoldenAtOneEMinus6()
    {
        foreach (var fixture in RaptorReferenceFixtures.Optimizer().Cases)
        {
            var coordinates = fixture.Initial.Select(row => row.ToArray()).ToArray();

            var next = fixture.InitialNextSample.ToArray();

            var negative = fixture.InitialNextNegative.ToArray();

            RaptorUmapOptimizer.RunEpoch(coordinates, fixture.Head, fixture.Tail, fixture.EpochsPerSample, next, fixture.EpochsPerNegativeSample, negative, fixture.Epoch, fixture.Alpha, fixture.A, fixture.B, fixture.Gamma, (edge, sample) => fixture.NegativeSamples[edge][sample], default);

            RaptorFuzzyGraphTests.Close(fixture.Expected.SelectMany(row => row), coordinates.SelectMany(row => row));

            RaptorFuzzyGraphTests.Close(fixture.NextSample, next);

            RaptorFuzzyGraphTests.Close(fixture.NextNegative, negative);
        }
    }

    [Fact]
    public void NegativeSamples_ActuallyRepelNonNeighbors()
    {
        double[][] coordinates = [[0, 0], [0, 0], [1, 0]];

        RaptorUmapOptimizer.RunEpoch(coordinates, [0], [1], [1], [1], [0.2], [0.2], 1, 0.25, 1, 1, 1, (_, _) => 2, default);

        Assert.True(coordinates[0][0] < -0.1);

        Assert.Equal(new double[] { 1, 0 }, coordinates[2]);
    }

    [Theory]
    [InlineData("manifold")]
    [InlineData("clustered")]
    [InlineData("disconnected")]
    [InlineData("duplicate")]
    public void Optimization_PreservesNeighborhoodsBetterThanInitialization(string kind)
    {
        var points = RaptorNeighborQualityTests.Points(kind, 160);

        var graph = Graph(points);

        var initial = RaptorUmapOptimizer.Initialize(points.Length, 2, new(751));

        var result = new RaptorUmapOptimizer().Optimize(graph, 2, 751, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        var final = result.Value.Select(point => point.Coordinates.ToArray()).ToArray();

        double before = Recall(points, initial);

        double after = Recall(points, final);

        output.WriteLine($"{kind}: initial recall@10={before:F5}, optimized={after:F5}");

        Assert.True(after >= 0.35 && after > before + 0.20, $"{kind}: {before} -> {after}");

        Assert.All(final.SelectMany(row => row), value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void Cancellation_StopsInsideEpoch()
    {
        using var cancellation = new CancellationTokenSource();

        RaptorUmapProgress observed = default;

        var progress = new SynchronousProgress(value => { if (value.SampledEdges > 0) { observed = value; cancellation.Cancel(); } });

        var graph = Graph(RaptorNeighborQualityTests.Points("manifold", 160));

        Assert.Throws<OperationCanceledException>(() => new RaptorUmapOptimizer(progress).Optimize(graph, 2, 1, cancellation.Token));

        Assert.Equal(1, observed.Epoch);

        Assert.InRange(observed.SampledEdges, 1, graph.Weights.Length - 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void EdgelessGraph_PreservesFiniteVertices(int count)
    {
        var graph = new RaptorFuzzyGraph([.. Enumerable.Range(0, count).Select(i => $"id-{i}")], [.. new int[count + 1]], [], [], [.. new double[count]], [.. Enumerable.Repeat(1d, count)]);

        var result = new RaptorUmapOptimizer().Optimize(graph, 2, 1, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(count, result.Value.Count);

        Assert.All(result.Value.SelectMany(point => point.Coordinates), value => Assert.True(double.IsFinite(value)));
    }

    [Fact]
    public void ShuffledInput_PreservesStableIdentitiesAndOutputCoordinates()
    {
        var points = RaptorNeighborQualityTests.Points("manifold", 60);

        var shuffled = points.Reverse().ToArray();

        var first = new RaptorUmapOptimizer().Optimize(Graph(points), 2, 17, default);

        var second = new RaptorUmapOptimizer().Optimize(Graph(shuffled), 2, 17, default);

        Assert.True(first.IsSuccess, first.Error.Code);

        Assert.True(second.IsSuccess, second.Error.Code);

        for (int index = 0; index < points.Length; index++)
        {
            Assert.Equal(index, first.Value[index].SourceIndex);

            Assert.Equal(points[index].StableId, first.Value[index].StableId);

            Assert.Equal(first.Value[index].StableId, second.Value[points.Length - index - 1].StableId);

            Assert.Equal(first.Value[index].Coordinates.ToArray(), second.Value[points.Length - index - 1].Coordinates.ToArray());
        }
    }

    [Fact]
    public void InvalidGraphOrDimensions_ReturnTypedRefusal()
    {
        var graph = new RaptorFuzzyGraph(["a", "b"], [0, 1, 2], [1, 0], [1, 1], [0, 0], [1, 1]);

        foreach (int dimensions in new[] { 0, -1, 11 }) Assert.Equal("Raptor.InvalidDimensions", new RaptorUmapOptimizer().Optimize(graph, dimensions, 1, default).Error.Code);

        foreach (var invalid in new[] { graph with { Weights = [double.NaN, 1] }, graph with { SourceIndices = [2, 0] }, graph with { RowOffsets = [0, 2, 1] }, graph with { Weights = [0.5, 1] }, graph with { StableIds = ["a", "a"] } }) Assert.Equal("Raptor.InvalidGraph", new RaptorUmapOptimizer().Optimize(invalid, 2, 1, default).Error.Code);
    }

    [Fact]
    public void Curve_UsesIndependentFitAndRefusesOtherRecipes()
    {
        var fixture = RaptorReferenceFixtures.Optimizer();

        var options = RaptorAlgorithmManifest.Current.Options;

        var curve = RaptorUmapOptimizer.ResolveCurve(options);

        Assert.True(curve.IsSuccess);

        Assert.Equal(fixture.FittedA, curve.Value.A);

        Assert.Equal(fixture.FittedB, curve.Value.B);

        foreach (var unsupported in new[] { options with { MinimumDistance = 0.2 }, options with { Spread = 2 }, options with { CurveA = double.NaN }, options with { CurveB = 1 }, options with { CurveRecipe = "unknown" } })
        {
            Assert.Equal("Raptor.UnsupportedCurve", RaptorUmapOptimizer.ResolveCurve(unsupported).Error.Code);
        }
    }

    [Fact]
    public void EpochSchedule_SkipsUntilDueAndCountsNegativeDrawsIncludingSelf()
    {
        double[][] coordinates = [[0, 0], [0, 0], [1, 0]];

        double[] next = [1, 2];

        double[] negative = [0.2, 0.4];

        var calls = new List<int>();

        Func<int, int, int> sample = (edge, _) => { calls.Add(edge); return edge; };

        var zero = RaptorUmapOptimizer.RunEpoch(coordinates, [0, 1], [1, 0], [1, 2], next, [0.2, 0.4], negative, 0, 1, 1, 1, 1, sample, default);

        Assert.Equal(0, zero.Edges);

        Assert.Empty(calls);

        var first = RaptorUmapOptimizer.RunEpoch(coordinates, [0, 1], [1, 0], [1, 2], next, [0.2, 0.4], negative, 1, 1, 1, 1, 1, sample, default);

        Assert.Equal(1, first.Edges);

        Assert.Equal(4, first.Negatives);

        Assert.Equal(new[] { 0, 0, 0, 0 }, calls);

        Assert.Equal(new[] { 2d, 2d }, next);

        Assert.Equal(new[] { 1d, 0.4 }, negative);

        var second = RaptorUmapOptimizer.RunEpoch(coordinates, [0, 1], [1, 0], [1, 2], next, [0.2, 0.4], negative, 2, 1, 1, 1, 1, sample, default);

        Assert.Equal(2, second.Edges);

        Assert.Equal(9, second.Negatives);

        Assert.Equal(new[] { 3d, 4d }, next);
    }

    [Fact]
    public void ProductionSampling_IsDeterministicSeededAndFollowsEpochSchedule()
    {
        var graph = new RaptorFuzzyGraph(["a", "b", "c"], [0, 2, 4, 6], [1, 2, 0, 2, 0, 1], [1, 1, 1, 1, 1, 1], [0, 0, 0], [1, 1, 1]);

        RaptorUmapProgress last = default;

        var observer = new SynchronousProgress(value => last = value);

        var first = new RaptorUmapOptimizer(observer).Optimize(graph, 2, 42, default);

        var repeat = new RaptorUmapOptimizer().Optimize(graph, 2, 42, default);

        var changed = new RaptorUmapOptimizer().Optimize(graph, 2, 43, default);

        Assert.True(first.IsSuccess && repeat.IsSuccess && changed.IsSuccess);

        Assert.Equal(first.Value.SelectMany(p => p.Coordinates), repeat.Value.SelectMany(p => p.Coordinates));

        Assert.NotEqual(first.Value.SelectMany(p => p.Coordinates).ToArray(), changed.Value.SelectMany(p => p.Coordinates).ToArray());

        Assert.Equal(499, last.Epoch);

        Assert.Equal(499 * 6, last.SampledEdges);

        Assert.InRange(last.NegativeSamples, 2493 * 6, 2494 * 6);

        output.WriteLine($"production epochs={last.Epoch + 1}, sampled={last.SampledEdges}, negatives={last.NegativeSamples}");
    }

    [Fact]
    public void Initialization_UsesPinnedSplitMixStreamAndCanonicalAxisRescale()
    {
        var actual = RaptorUmapOptimizer.Initialize(3, 2, new(42));

        // Independently calculated from published SplitMix64 seed42 outputs, row-major then per-axis rescale.
        double[][] expected = [[10, 0], [3.4194611611373884, 2.6016620493223934], [0, 10]];

        RaptorFuzzyGraphTests.Close(expected.SelectMany(row => row), actual.SelectMany(row => row));
    }

    [Fact]
    public void CoincidentAndExtremeFiniteCoordinates_StayFiniteWithClippedGradients()
    {
        double[][] coordinates = [[0, 0], [double.Epsilon, 0], [double.MaxValue, 0]];

        RaptorUmapOptimizer.RunEpoch(coordinates, [0], [1], [1], [1], [0.2], [0.2], 1, 1, 1, 1, 1, (_, _) => 2, default);

        Assert.All(coordinates.SelectMany(row => row), x => Assert.True(double.IsFinite(x)));

        double[][] close = [[0, 0], [0.001, 0]];

        RaptorUmapOptimizer.RunEpoch(close, [0], [1], [1], [1], [0.2], [1], 1, 0.25, 1e20, 1, 1, (_, _) => 0, default);

        Assert.Equal(1, close[0][0]);

        Assert.Equal(-0.999, close[1][0], 12);
    }

    [Fact]
    public void PortLicense_IsCarriedIntoBuildOutput()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "licenses", "umap-learn-LICENSE.txt");

        Assert.True(File.Exists(path), "The port's license must accompany binary output.");
    }

    [Fact]
    public void OptimizerAllocation_ScalesWithSparseEdges()
    {
        long Measure(int count)
        {
            var graph = new RaptorFuzzyGraphBuilder().Build(RaptorFuzzyGraphTests.Ring(count), default).Value;

            long start = GC.GetAllocatedBytesForCurrentThread();

            var result = new RaptorUmapOptimizer().Optimize(graph, 2, 42, default);

            long bytes = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.True(result.IsSuccess, result.Error.Code);

            Assert.Equal(count, result.Value.Count);

            Assert.All(result.Value.SelectMany(p => p.Coordinates), value => Assert.True(double.IsFinite(value)));

            output.WriteLine($"optimizer n={count}, edges={graph.Weights.Length}, allocated={bytes}");

            return bytes;
        }

        _ = Measure(20);

        long small = Measure(600);

        long large = Measure(2400);

        Assert.True(large < small * 5);

        Assert.True(large < 12_000_000, $"Optimizer allocated {large} bytes; sparse storage required.");
    }

    internal static RaptorFuzzyGraph Graph(RaptorPoint[] points)
    {
        var neighbors = new RaptorNeighborGraphBuilder().Build(points, Math.Min(12, points.Length), 1, default);

        Assert.True(neighbors.IsSuccess, neighbors.Error.Code);

        var graph = new RaptorFuzzyGraphBuilder().Build(neighbors.Value, default);

        Assert.True(graph.IsSuccess, graph.Error.Code);

        return graph.Value;
    }

    private static double Recall(RaptorPoint[] points, double[][] coordinates)
    {
        int hits = 0;

        for (int row = 0; row < points.Length; row++)
        {
            var vector = points[row].Vector.ToArray();

            double norm = Math.Sqrt(vector.Sum(x => (double)x * x));

            var expected = Enumerable.Range(0, points.Length).Where(column => column != row).OrderBy(column =>
            {
                var other = points[column].Vector.ToArray();

                return 1 - vector.Select((x, d) => (double)x * other[d]).Sum() / (norm * Math.Sqrt(other.Sum(x => (double)x * x)));
            }).ThenBy(column => points[column].StableId, StringComparer.Ordinal).Take(10).ToHashSet();

            var actual = Enumerable.Range(0, points.Length).Where(column => column != row).OrderBy(column => coordinates[row].Select((x, d) => Math.Pow(x - coordinates[column][d], 2)).Sum()).ThenBy(column => points[column].StableId, StringComparer.Ordinal).Take(10);

            hits += actual.Count(expected.Contains);
        }

        return hits / (10d * points.Length);
    }

    private sealed class SynchronousProgress(Action<RaptorUmapProgress> action) : IProgress<RaptorUmapProgress>
    {
        public void Report(RaptorUmapProgress value) => action(value);
    }
}
