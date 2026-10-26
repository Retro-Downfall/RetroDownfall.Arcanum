using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorSmallMatrixTests
{
    [Fact]
    public void LogDensity_MatchesCorrelatedGaussian()
    {
        var fixture = RaptorReferenceFixtures.Gaussian();

        for (int component = 0; component < fixture.Weights.Length; component++)
        {
            var factor = RaptorSmallMatrix.Cholesky(fixture.Covariances[component], default);

            Assert.True(factor.IsSuccess, factor.Error.Code);

            Close(fixture.Cholesky[component].SelectMany(r => r), factor.Value.SelectMany(r => r));

            Assert.Equal(fixture.LogDeterminants[component], RaptorSmallMatrix.LogDeterminant(factor.Value), 8);

            for (int row = 0; row < fixture.Points.Length; row++)
                Assert.Equal(fixture.LogPdf[row][component], RaptorSmallMatrix.LogDensity(fixture.Points[row], fixture.Means[component], factor.Value, default), 8);
        }

        var a = fixture.Analytic;

        var lower = RaptorSmallMatrix.Cholesky(a.Covariance, default).Value;

        Assert.Equal(a.LogPdf, RaptorSmallMatrix.LogDensity(a.Point, a.Mean, lower, default), 8);
    }

    [Fact]
    public void Cholesky_RejectsSingularNonfiniteAsymmetricAndInvalidShape()
    {
        foreach (double[][] matrix in new double[][][] { [[1, 1], [1, 1]], [[1, 2], [2, 1]], [[1, 0], [1, 1]], [[double.NaN]], [[1, 0]], [] })
            Assert.True(RaptorSmallMatrix.Cholesky(matrix, default).IsFailure);

        using var c = new CancellationTokenSource();

        c.Cancel();

        Assert.Throws<OperationCanceledException>(() => RaptorSmallMatrix.Cholesky([[1]], c.Token));
    }

    internal static void Close(IEnumerable<double> expected, IEnumerable<double> actual, double tolerance = 1e-8)
    {
        var e = expected.ToArray();

        var a = actual.ToArray();

        Assert.Equal(e.Length, a.Length);

        for (int i = 0; i < e.Length; i++) Assert.True(double.IsFinite(a[i]) && Math.Abs(e[i] - a[i]) <= tolerance * Math.Max(1, Math.Abs(e[i])), $"{i}: expected {e[i]:R}, actual {a[i]:R}");
    }
}
