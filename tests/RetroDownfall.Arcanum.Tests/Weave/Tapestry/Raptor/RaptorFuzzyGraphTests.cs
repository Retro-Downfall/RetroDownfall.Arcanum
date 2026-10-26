using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorFuzzyGraphTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void SmoothDistances_MatchPinnedRhoSigma()
    {
        foreach (var fixture in RaptorReferenceFixtures.Fuzzy().Cases)
        {
            var result = new RaptorFuzzyGraphBuilder().Build(Neighbors(fixture), default);

            Assert.True(result.IsSuccess, result.Error.Code);

            Close(fixture.Rhos, result.Value.Rhos);

            Close(fixture.Sigmas, result.Value.Sigmas);
        }
    }

    [Fact]
    public void FuzzyUnion_UsesAPlusBMinusAB()
    {
        foreach (var fixture in RaptorReferenceFixtures.Fuzzy().Cases)
        {
            var result = new RaptorFuzzyGraphBuilder().Build(Neighbors(fixture), default);

            Assert.True(result.IsSuccess, result.Error.Code);

            var graph = result.Value;

            for (int row = 0; row < graph.PointCount; row++)
            {
                double[] actual = new double[graph.PointCount];

                for (int edge = graph.RowOffsets[row]; edge < graph.RowOffsets[row + 1]; edge++)
                {
                    actual[graph.SourceIndices[edge]] = graph.Weights[edge];
                }
                Close(fixture.Symmetric[row], actual);
            }
        }
    }

    [Fact]
    public void DuplicateDistances_StayFinite()
    {
        var fixture = RaptorReferenceFixtures.Fuzzy().Cases.Single(item => item.Name == "all-duplicates");

        var result = new RaptorFuzzyGraphBuilder().Build(Neighbors(fixture), default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(4, result.Value.PointCount);

        Assert.All(result.Value.Sigmas, sigma => Assert.True(double.IsFinite(sigma) && sigma > 0));

        Assert.All(result.Value.Weights, weight => Assert.Equal(1, weight));
    }

    [Fact]
    public void InvalidNeighborGraphs_RefuseWithoutPartialOutput()
    {
        var graph = Neighbors(RaptorReferenceFixtures.Fuzzy().Cases[0]);

        RaptorNeighborGraph[] invalid = [null!, graph with { Neighbors = 0 }, graph with { PointCount = -1 }, graph with { RowOffsets = new int[] { 0 } }, graph with { StableIds = default }];

        foreach (var candidate in invalid)
        {
            Assert.Equal("Raptor.InvalidGraph", new RaptorFuzzyGraphBuilder().Build(candidate!, default).Error.Code);
        }

        var distances = graph.Distances.ToArray();

        distances[1] = double.NaN;

        Assert.Equal("Raptor.InvalidGraph", new RaptorFuzzyGraphBuilder().Build(graph with { Distances = distances }, default).Error.Code);

        var indices = graph.SourceIndices.ToArray();

        indices[1] = indices[0];

        Assert.Equal("Raptor.InvalidGraph", new RaptorFuzzyGraphBuilder().Build(graph with { SourceIndices = indices }, default).Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TinyGraphs_PreserveEveryVertexWithoutEdges(int count)
    {
        var points = Enumerable.Range(0, count).Select(i => new RaptorPoint($"id-{i}", new float[] { 1 })).ToArray();

        var neighbors = new RaptorNeighborGraphBuilder().Build(points, 1, 0, default).Value;

        var result = new RaptorFuzzyGraphBuilder().Build(neighbors, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(count, result.Value.PointCount);

        Assert.Equal(count + 1, result.Value.RowOffsets.Length);

        Assert.Empty(result.Value.Weights);
    }

    [Fact]
    public void Cancellation_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => new RaptorFuzzyGraphBuilder().Build(Neighbors(RaptorReferenceFixtures.Fuzzy().Cases[0]), cancellation.Token));
    }

    [Fact]
    public void SparseAllocation_ScalesWithEdgesAndPreservesVertices()
    {
        long Measure(int count)
        {
            var graph = Ring(count);

            long before = GC.GetAllocatedBytesForCurrentThread();

            var result = new RaptorFuzzyGraphBuilder().Build(graph, default);

            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(result.IsSuccess, result.Error.Code);

            Assert.Equal(count, result.Value.PointCount);

            Assert.Equal(4 * count, result.Value.Weights.Length);

            Assert.All(result.Value.Weights, x => Assert.True(x > 0 && x <= 1));

            output.WriteLine($"n={count} k=3 edges={result.Value.Weights.Length} allocated={bytes}");

            return bytes;
        }
        _ = Measure(20);

        long small = Measure(2000);

        long large = Measure(8000);

        Assert.True(large < small * 5, $"Allocation {small} -> {large} must scale with sparse edges.");

        Assert.True(large < 16_000_000, $"Sparse graph allocated {large} bytes.");
    }

    [Fact]
    public void Underflow_DropsOnlyZeroWeightEdgesAndNeverVertices()
    {
        var graph = new RaptorNeighborGraph(10, 5, Enumerable.Range(0, 11).Select(i => i * 5).ToArray(), Enumerable.Range(0, 10).SelectMany(i => new[] { i, (i + 1) % 10, (i + 2) % 10, (i + 3) % 10, (i + 4) % 10 }).ToArray(), Enumerable.Range(0, 10).SelectMany(_ => new[] { 0d, 0d, 0.001, 0.001, 2d }).ToArray(), 0, 0, [.. Enumerable.Range(0, 10).Select(i => $"id-{i}")]);

        var result = new RaptorFuzzyGraphBuilder().Build(graph, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(10, result.Value.PointCount);

        Assert.Equal(60, result.Value.Weights.Length);

        Assert.All(result.Value.Weights, x => Assert.Equal(1, x));

        Assert.All(result.Value.Weights, x => Assert.True(double.IsFinite(x) && x > 0));
    }

    [Fact]
    public void GraphIdentity_IsFrozenAndInvalidUnicodeIsRefused()
    {
        var points = new[] { new RaptorPoint("a", new float[] { 1, 0 }), new RaptorPoint("b", new float[] { 0, 1 }) };

        var neighbors = new RaptorNeighborGraphBuilder().Build(points, 2, 1, default).Value;

        points[0] = new("changed", new float[] { 1, 0 });

        var result = new RaptorFuzzyGraphBuilder().Build(neighbors, default);

        Assert.True(result.IsSuccess);

        Assert.Equal("a", result.Value.StableIds[0]);

        Assert.Equal("Raptor.InvalidGraph", new RaptorFuzzyGraphBuilder().Build(neighbors with { StableIds = ["a", "\ud800"] }, default).Error.Code);
    }

    internal static RaptorNeighborGraph Ring(int count) => new(count, 3, Enumerable.Range(0, count + 1).Select(i => i * 3).ToArray(), Enumerable.Range(0, count).SelectMany(i => new[] { i, (i + 1) % count, (i + 2) % count }).ToArray(), Enumerable.Range(0, count).SelectMany(_ => new[] { 0d, 0.1, 0.2 }).ToArray(), 0, 0, [.. Enumerable.Range(0, count).Select(i => $"point-{i:D6}")]);

    internal static RaptorNeighborGraph Neighbors(RaptorFuzzyCase fixture) => new(fixture.Points.Length, fixture.Neighbors, Enumerable.Range(0, fixture.Points.Length + 1).Select(i => i * fixture.Neighbors).ToArray(), fixture.Indices.SelectMany(row => row).ToArray(), fixture.Distances.SelectMany(row => row).ToArray(), 0, 0, [.. Enumerable.Range(0, fixture.Points.Length).Select(i => $"point-{i:D3}")]);

    internal static void Close(IEnumerable<double> expected, IEnumerable<double> actual)
    {
        Assert.Equal(expected.Count(), actual.Count());

        foreach (var (left, right) in expected.Zip(actual)) Assert.InRange(Math.Abs(left - right), 0, 1e-6);
    }
}
