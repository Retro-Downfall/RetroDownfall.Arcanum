using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorNeighborGraphTests
{
    [Fact]
    public void ExactGraph_MatchesOracleWithTiesAndDuplicates()
    {
        RaptorPoint[] points = [new("d", new float[] { 0, 1 }), new("b", new float[] { 1, 0 }), new("a", new float[] { 1, 0 }), new("c", new float[] { -1, 0 })];

        var result = new RaptorNeighborGraphBuilder().Build(points, 4, 42, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        var graph = result.Value;

        Assert.Equal(new[] { 0, 4, 8, 12, 16 }, graph.RowOffsets.ToArray());

        Assert.Equal(new[] { 0, 2, 1, 3, 1, 2, 0, 3, 2, 1, 0, 3, 3, 0, 2, 1 }, graph.SourceIndices.ToArray());

        Assert.Equal(new double[] { 0, 1, 1, 1, 0, 0, 1, 2, 0, 0, 1, 2, 0, 1, 2, 2 }, graph.Distances.ToArray());
    }

    [Fact]
    public void ExactGraph_MatchesPinnedReferenceFixture()
    {
        RaptorFuzzyFixture fixture = RaptorReferenceFixtures.Fuzzy();

        foreach (RaptorFuzzyCase sample in fixture.Cases)
        {
            var points = sample.Points.Select((vector, index) => new RaptorPoint($"point-{index:D3}", vector.Select(value => (float)value).ToArray())).ToArray();

            var result = new RaptorNeighborGraphBuilder().Build(points, sample.Neighbors, 224, default);

            Assert.True(result.IsSuccess, result.Error.Code);

            Assert.Equal(sample.Indices.SelectMany(row => row), result.Value.SourceIndices.ToArray());

            double[] expected = sample.Distances.SelectMany(row => row).ToArray();

            for (int edge = 0; edge < expected.Length; edge++)
            {
                Assert.InRange(Math.Abs(expected[edge] - result.Value.Distances.Span[edge]), 0, fixture.Tolerance);
            }
        }
    }

    [Fact]
    public void IdenticalNonAxisVectors_HaveExactlyZeroDistance()
    {
        RaptorPoint[] points = [new("a", new float[] { 1, 1 }), new("b", new float[] { 1, 1 })];

        var result = new RaptorNeighborGraphBuilder().Build(points, 2, 0, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.All(result.Value.Distances.ToArray(), distance => Assert.Equal(0, distance));
    }

    [Fact]
    public void Cosine_IgnoresMagnitudeAndHandlesExtremeFiniteScales()
    {
        RaptorPoint[] points = [new("a", new float[] { float.MaxValue, 0 }), new("b", new float[] { float.Epsilon, float.Epsilon }), new("c", new float[] { 0, 5 })];

        var result = new RaptorNeighborGraphBuilder().Build(points, 3, 0, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(new[] { 0, 1, 2 }, result.Value.SourceIndices.Slice(0, 3).ToArray());

        Assert.InRange(Math.Abs(result.Value.Distances.Span[1] - (1 - Math.Sqrt(0.5))), 0, 1e-15);

        Assert.Equal(1, result.Value.Distances.Span[2]);
    }

    [Fact]
    public void DuplicateVectors_RetainDistinctIndices()
    {
        var points = Enumerable.Range(0, 12).Select(i => new RaptorPoint($"id-{i:D3}", new float[] { 2, 0 })).ToArray();

        var result = new RaptorNeighborGraphBuilder().Build(points, 12, 7, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        for (int row = 0; row < points.Length; row++)
        {
            Assert.Equal(row, result.Value.SourceIndices.Span[row * 12]);

            Assert.Equal(12, result.Value.SourceIndices.Slice(row * 12, 12).ToArray().Distinct().Count());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TinyInputs_AreExplicit(int count)
    {
        RaptorPoint[] points = count == 0 ? [] : [new("a", new float[] { 1 })];

        var result = new RaptorNeighborGraphBuilder().Build(points, 1, 0, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(count, result.Value.SourceIndices.Length);

        Assert.Equal(count + 1, result.Value.RowOffsets.Length);
    }

    [Fact]
    public void InvalidInput_RefusesWithoutDroppingSamples()
    {
        RaptorPoint valid = new("a", new float[] { 1, 0 });

        RaptorPoint[][] inputs = [null!, [null!], [new("empty", Array.Empty<float>())], [valid, valid], [valid, new("b", new float[] { 0, 0 })], [valid, new("b", new float[] { float.NaN, 1 })], [valid, new("b", new float[] { float.PositiveInfinity, 1 })], [valid, new("b", new float[] { 1 })], [valid, new(" ", new float[] { 1, 0 })], [valid, new("\ud800", new float[] { 1, 0 })]];

        foreach (var input in inputs)
        {
            var result = new RaptorNeighborGraphBuilder().Build(input, 1, 0, default);

            Assert.True(result.IsFailure);

            Assert.Equal("Raptor.InvalidInput", result.Error.Code);
        }
        Assert.Equal("Raptor.InvalidNeighbors", new RaptorNeighborGraphBuilder().Build([valid], 0, 0, default).Error.Code);

        Assert.Equal("Raptor.InvalidNeighbors", new RaptorNeighborGraphBuilder().Build([valid], 2, 0, default).Error.Code);
    }

    [Theory]
    [InlineData(80, "clustered")]
    [InlineData(2200, "clustered")]
    [InlineData(2200, "proportional")]
    [InlineData(2200, "identical")]
    public void ShuffledInput_PreservesNeighborIdentitiesAndOriginalIndexMapping(int count, string kind)
    {
        var points = RaptorNeighborQualityTests.Points(kind, count);

        var shuffled = points.Reverse().ToArray();

        var first = new RaptorNeighborGraphBuilder().Build(points, 12, 99, default);

        var second = new RaptorNeighborGraphBuilder().Build(shuffled, 12, 99, default);

        Assert.True(first.IsSuccess, first.Error.Code);

        Assert.True(second.IsSuccess, second.Error.Code);

        for (int row = 0; row < count; row++)
        {
            int other = count - row - 1;

            Assert.Equal(first.Value.SourceIndices.Slice(row * 12, 12).ToArray().Select(i => points[i].StableId), second.Value.SourceIndices.Slice(other * 12, 12).ToArray().Select(i => shuffled[i].StableId));

            Assert.Equal(first.Value.Distances.Slice(row * 12, 12).ToArray(), second.Value.Distances.Slice(other * 12, 12).ToArray());
        }
    }

    [Theory]
    [InlineData(100, "exact")]
    [InlineData(2200, "descent")]
    public void Cancellation_StopsInsideCandidateLoop(int count, string phase)
    {
        using CancellationTokenSource source = new();

        long observed = 0;

        var observer = new SynchronousProgress(value =>
        {
            if (value.Phase == phase && value.DistanceEvaluations > 0)
            {
                observed = value.DistanceEvaluations;

                source.Cancel();
            }
        });

        Assert.Throws<OperationCanceledException>(() => new RaptorNeighborGraphBuilder(observer).Build(RaptorNeighborQualityTests.Points("clustered", count), 12, 9, source.Token));

        Assert.True(observed > 0);
    }

    private sealed class SynchronousProgress(Action<RaptorNeighborProgress> action) : IProgress<RaptorNeighborProgress>
    {
        public void Report(RaptorNeighborProgress value) => action(value);
    }
}
