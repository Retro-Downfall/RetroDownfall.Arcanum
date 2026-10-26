using RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorGaussianMixtureTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Responsibilities_SumToOneAndMatchIndependentOverlapGolden()
    {
        var f = RaptorReferenceFixtures.Gaussian();

        var model = RaptorGaussianMixture.FromParameters(f.Weights, f.Means, f.Covariances);

        Assert.True(model.IsSuccess, model.Error.Code);

        for (int row = 0; row < f.Points.Length; row++)
        {
            double[] p = new double[f.Weights.Length];

            Assert.True(model.Value.Posterior(f.Points[row], p).IsSuccess);

            RaptorSmallMatrixTests.Close(f.Responsibilities[row], p);

            Assert.Equal(1, p.Sum(), 12);

            Assert.Equal(f.Memberships[row], p.Select((v, i) => (v, i)).Where(x => x.v > f.Threshold).Select(x => x.i));
        }
    }

    [Fact]
    public void SuppliedInitialization_MatchesFrozenEmLikelihoodBicAndFullCovariances()
    {
        var f = RaptorReferenceFixtures.Gaussian();

        foreach (var expected in f.Fits)
        {
            var result = new RaptorGaussianMixture().FitInitialized(f.EmPoints, expected.InitialWeights, expected.InitialMeans, expected.InitialCovariances, default);

            Assert.True(result.IsSuccess, result.Error.Code);

            var fit = result.Value;

            Assert.True(fit.Converged);

            output.WriteLine($"frozen K={expected.Components}; iterations={fit.Iterations}; likelihood={fit.TotalLogLikelihood:R}; BIC={fit.Bic:R}");

            Assert.Equal(expected.Iterations, fit.Iterations);

            RaptorSmallMatrixTests.Close([expected.TotalLogLikelihood, expected.Bic], [fit.TotalLogLikelihood, fit.Bic], 1e-6);

            RaptorSmallMatrixTests.Close(expected.Covariances.SelectMany(m => m.SelectMany(r => r)), fit.Covariances.SelectMany(m => m.SelectMany(r => r)), 1e-6);

            for (int i = 0; i < f.EmPoints.Length; i++)
            {
                double[] p = new double[expected.Components];

                Assert.True(fit.Posterior(f.EmPoints[i], p).IsSuccess);

                RaptorSmallMatrixTests.Close(expected.Responsibilities[i], p, 1e-6);
            }
        }
    }

    [Fact]
    public void Bic_CountsFullCovarianceParameters() => Assert.Equal(115.44966506492696, RaptorGaussianMixture.CalculateBic(-42.475594560144685, 16, 2, 2), 10);

    [Fact]
    public void Selection_UsesExclusiveReferenceUpperBound()
    {
        var seen = new HashSet<int>();

        var gmm = new RaptorGaussianMixture(new Observer(p => seen.Add(p.Components)));

        var result = gmm.SelectByBic([[0, 0], [1, 1]], 1, default);

        Assert.True(result.IsSuccess, result.Error.Code);

        Assert.Equal(new[] { 1 }, seen.Order());

        Assert.Single(result.Value.Weights);
    }

    [Fact]
    public void ModelSelection_FindsSeparatedMixturesAndReusesTheWinningFit()
    {
        var points = Separated();

        var selected = new RaptorGaussianMixture().SelectByBic(points, 42, default);

        Assert.True(selected.IsSuccess, selected.Error.Code);

        Assert.Equal(2, selected.Value.Weights.Length);

        output.WriteLine($"Gaussian selection K={selected.Value.Weights.Length}; BIC={selected.Value.Bic:R}");

        Assert.Equal(7127.796375012894, selected.Value.Bic, 6);

        var fitted = new RaptorGaussianMixture().Fit(points, 2, 42, default);

        Assert.True(fitted.IsSuccess, fitted.Error.Code);

        Assert.Equal(selected.Value.Bic, fitted.Value.Bic);

        Assert.Equal(selected.Value.Means.SelectMany(r => r), fitted.Value.Means.SelectMany(r => r));
    }

    [Fact]
    public void IdenticalPoints_ReturnFiniteOneComponent()
    {
        foreach (int n in new[] { 1, 2, 12 })
        {
            var result = new RaptorGaussianMixture().SelectByBic(Enumerable.Range(0, n).Select(_ => new[] { 3d, -2 }).ToArray(), 42, default);

            Assert.True(result.IsSuccess, result.Error.Code);

            Assert.Single(result.Value.Weights);

            Assert.True(double.IsFinite(result.Value.TotalLogLikelihood));

            double[] posterior = [0];

            Assert.True(result.Value.Posterior([3, -2], posterior).IsSuccess);

            Assert.Equal(1, posterior[0]);

            Assert.Equal(1e-6, result.Value.Covariances[0][0][0]);

            Assert.Equal(-n * Math.Log(2 * Math.PI * 1e-6), result.Value.TotalLogLikelihood, 8);
        }
    }

    [Fact]
    public void SingularCandidate_IsRejectedWithoutHardFallback()
    {
        var result = new RaptorGaussianMixture().FitInitialized([[0, 0], [1, 1]], [1], [[0, 0]], [[[1, 1], [1, 1]]], default);

        Assert.Equal("Raptor.SingularCovariance", result.Error.Code);

        var empty = new RaptorGaussianMixture().FitInitialized([[0, 0], [1, 1]], [0.5, 0.5], [[0, 0], [1e10, 1e10]], [[[1, 0], [0, 1]], [[1, 0], [0, 1]]], default);

        Assert.Equal("Raptor.VanishingComponent", empty.Error.Code);
    }

    [Fact]
    public void NonconvergedCandidates_AreNotSelected()
    {
        var f = RaptorReferenceFixtures.Gaussian();

        var initial = f.Fits[1];

        Assert.Equal("Raptor.Nonconverged", new RaptorGaussianMixture().FitInitialized(f.EmPoints, initial.InitialWeights, initial.InitialMeans, initial.InitialCovariances, default, 1).Error.Code);

        Assert.Equal("Raptor.NoConvergedFit", new RaptorGaussianMixture().SelectByBic(f.EmPoints, 42, default, 1).Error.Code);
    }

    [Fact]
    public void Cancellation_InterruptsEmBatch()
    {
        using var c = new CancellationTokenSource();

        int samples = 0;

        var gmm = new RaptorGaussianMixture(new Observer(p => { if (p.Iteration > 0 && p.Samples > 0) { samples = p.Samples; c.Cancel(); } }));

        Assert.Throws<OperationCanceledException>(() => gmm.Fit(Separated(200), 2, 42, c.Token));

        Assert.InRange(samples, 1, 199);
    }

    [Fact]
    public void InvalidStates_AreTypedRefusals()
    {
        foreach (double[][] points in new double[][][] { [], [[double.NaN]], [[double.PositiveInfinity]], [[1], [1, 2]], [[double.MaxValue], [-double.MaxValue]], [new double[11]] })
            Assert.True(new RaptorGaussianMixture().SelectByBic(points, 1, default).IsFailure);

        foreach (double[] weights in new double[][] { [-1, 2], [0.2, 0.2], [double.NaN, 1], [0, 1] })
            Assert.True(RaptorGaussianMixture.FromParameters(weights, [[0], [1]], [[[1]], [[1]]]).IsFailure);

        var f = RaptorReferenceFixtures.Gaussian();

        var fit = RaptorGaussianMixture.FromParameters(f.Weights, f.Means, f.Covariances).Value;

        Assert.True(fit.Posterior([double.NaN, 0], new double[f.Weights.Length]).IsFailure);

        Assert.True(fit.Posterior([0, 0], new double[1]).IsFailure);

        Assert.True(fit.Posterior([double.MaxValue, double.MaxValue], new double[f.Weights.Length]).IsFailure);
    }

    [Fact]
    public void Permutations_PreserveCanonicalFitAndDuplicateMultiplicity()
    {
        var points = Separated();

        var first = new RaptorGaussianMixture().Fit(points, 2, 42, default);

        var second = new RaptorGaussianMixture().Fit(points.Reverse().ToArray(), 2, 42, default);

        Assert.True(first.IsSuccess && second.IsSuccess);

        Assert.Equal(first.Value.TotalLogLikelihood, second.Value.TotalLogLikelihood);

        Assert.Equal(first.Value.Means.SelectMany(r => r), second.Value.Means.SelectMany(r => r));

        for (int n = 2; n <= 12; n++) Assert.True(new RaptorGaussianMixture().Fit(Enumerable.Range(0, n).Select(i => new[] { (double)i, i * 1e-10 }).ToArray(), 1, 42, default).IsSuccess);
    }

    [Fact]
    public void Selection_DiagnosesEveryCandidateWithoutRetainingFits()
    {
        var candidates = new List<RaptorGmmCandidate>();

        var gmm = new RaptorGaussianMixture(diagnostics: new CandidateObserver(candidates.Add));

        var result = gmm.SelectByBic(Separated(60), 42, default, 1);

        Assert.Equal("Raptor.NoConvergedFit", result.Error.Code);

        Assert.Equal(Enumerable.Range(1, 49), candidates.Select(c => c.Components));

        Assert.All(candidates, c => { Assert.Equal("Raptor.Nonconverged", c.RefusalCode); Assert.Null(c.Bic); });
    }

    [Fact]
    public void Allocation_ScalesWithSamplesWithoutPosteriorTensor()
    {
        long Measure(int count)
        {
            var points = Separated(count);

            long start = GC.GetAllocatedBytesForCurrentThread();

            var fit = new RaptorGaussianMixture().Fit(points, 2, 42, default);

            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.True(fit.IsSuccess, fit.Error.Code);

            return allocated;
        }

        _ = Measure(50);

        long small = Measure(400);

        long large = Measure(1600);

        output.WriteLine($"allocation400={small};allocation1600={large}");

        Assert.True(large < small * 5, $"400={small};1600={large}");

        Assert.True(large < 1_000_000, $"1600={large}");
    }

    [Fact]
    public void Fit_FreezesParametersAndValidatesPosteriorShape()
    {
        double[] weights = [1];

        double[][] means = [[0, 0]];

        double[][][] covariance = [[[2, 1], [1, 2]]];

        var fit = RaptorGaussianMixture.FromParameters(weights, means, covariance).Value;

        weights[0] = 0;

        means[0][0] = 100;

        covariance[0][0][0] = -1;

        double[] posterior = [0];

        Assert.True(fit.Posterior([1, 1], posterior).IsSuccess);

        Assert.Equal(1, posterior[0]);

        Assert.Equal(0, fit.Means[0][0]);

        Assert.Equal(2, fit.Covariances[0][0][0]);

        Assert.True(fit.Posterior([0], posterior).IsFailure);
    }

    private sealed class CandidateObserver(Action<RaptorGmmCandidate> action) : IProgress<RaptorGmmCandidate> { public void Report(RaptorGmmCandidate value) => action(value); }
    // Independently reproduced in pinned sklearn 1.4.2: 1000 points, BIC minimum K2.
    private static double[][] Separated(int count = 1000)
    {
        var random = new TapestryDeterministicRandom(314159);

        return Enumerable.Range(0, count).Select(i =>
        {
            double radius = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble()));

            double angle = 2 * Math.PI * random.NextDouble();

            return new[] { (i % 2 == 0 ? -20d : 20d) + radius * Math.Cos(angle), radius * Math.Sin(angle) };
        }).ToArray();
    }

    private sealed class Observer(Action<RaptorGmmProgress> action) : IProgress<RaptorGmmProgress> { public void Report(RaptorGmmProgress value) => action(value); }
}
