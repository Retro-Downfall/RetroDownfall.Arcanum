using System.Buffers.Binary;

using System.Collections.Immutable;

using System.Globalization;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>Immutable, canonically ordered converged parameters; point posteriors are evaluated on demand.</summary>
public sealed class RaptorGmmFit
{
    private readonly RaptorGaussianMixture.Parameters _parameters;

    internal RaptorGmmFit(RaptorGaussianMixture.Parameters parameters, double likelihood, double bic, int iterations)
    {
        _parameters = parameters;

        Weights = [.. parameters.Weights];

        Means = [.. parameters.Means.Select(row => row.ToImmutableArray())];

        Covariances = [.. parameters.Covariances.Select(matrix => matrix.Select(row => row.ToImmutableArray()).ToImmutableArray())];

        TotalLogLikelihood = likelihood;

        Bic = bic;

        Iterations = iterations;
    }

    public ImmutableArray<double> Weights { get; }

    public ImmutableArray<ImmutableArray<double>> Means { get; }

    public ImmutableArray<ImmutableArray<ImmutableArray<double>>> Covariances { get; }

    public double TotalLogLikelihood { get; }

    public double Bic { get; }

    public int Iterations { get; }

    public bool Converged => true;

    /// <summary>Returns the point log likelihood and fills exactly one normalized posterior row, or refuses.</summary>
    public Result<double> Posterior(ReadOnlySpan<double> point, Span<double> destination)
    {
        if (point.Length != Means[0].Length || destination.Length != Weights.Length) return Invalid();

        foreach (double value in point) if (!double.IsFinite(value)) return Invalid();

        if (!RaptorGaussianMixture.Posterior(_parameters, point, destination, default, out double likelihood))
        {
            destination.Clear();

            return Invalid();
        }

        return likelihood;
    }

    private static Error Invalid() => new("Raptor.InvalidPosterior", "Posterior input or arithmetic is invalid.");
}

/// <summary>Content-free progress after an actual E/M sample block, including attempted candidate identity.</summary>
public readonly record struct RaptorGmmProgress(int Components, int Iteration, int Samples);

/// <summary>Bounded, content-free outcome of one attempted BIC candidate; no fit or sample content is retained.</summary>
public sealed record RaptorGmmCandidate(int Components, string? RefusalCode, double? Bic)
{
    /// <summary>The actual identical-row one-component fit bypassed candidate model selection.</summary>
    public bool SearchBypassed { get; init; }
}

/// <summary>
/// Original managed full-covariance EM implementation of standard Gaussian-mixture equations.
/// Pinned sklearn fixtures verify equations and stopping semantics; no upstream source is copied.
/// Canonical sample order, weighted Welford moments and bounded posterior blocks preserve multiplicity.
/// </summary>
public sealed class RaptorGaussianMixture(IProgress<RaptorGmmProgress>? progress = null, IProgress<RaptorGmmCandidate>? diagnostics = null)
{
    internal IProgress<RaptorGmmCandidate>? Diagnostics { get; } = diagnostics;

    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    public Result<RaptorGmmFit> Fit(double[][] points, int components, ulong seed, CancellationToken cancellationToken)
    {
        var prepared = Prepare(points, cancellationToken);

        if (prepared.IsFailure) return prepared.Error;

        return FitPrepared(prepared.Value, components, seed, cancellationToken, Options.MaximumEmIterations);
    }

    public Result<RaptorGmmFit> SelectByBic(double[][] points, ulong seed, CancellationToken cancellationToken) => SelectByBic(points, seed, cancellationToken, Options.MaximumEmIterations);

    internal Result<RaptorGmmFit> SelectByBic(double[][] points, ulong seed, CancellationToken cancellationToken, int maximumIterations)
    {
        var prepared = Prepare(points, cancellationToken);

        if (prepared.IsFailure) return prepared.Error;

        double[][] ordered = prepared.Value;

        if (CompareRows(ordered[0], ordered[^1]) == 0)
        {
            var single = FitPrepared(ordered, 1, seed, cancellationToken, maximumIterations);

            if (Diagnostics is { } observer)
            {
                observer.Report(new(1, single.IsFailure ? single.Error.Code : null, single.IsSuccess ? single.Value.Bic : null)
                {
                    SearchBypassed = true,
                });

                cancellationToken.ThrowIfCancellationRequested();
            }

            return single;
        }

        RaptorGmmFit? best = null;

        for (int components = 1; components < Math.Min(Options.ComponentUpperBoundExclusive, ordered.Length); components++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = FitPrepared(ordered, components, seed, cancellationToken, maximumIterations);

            Diagnostics?.Report(new(components, candidate.IsFailure ? candidate.Error.Code : null, candidate.IsSuccess ? candidate.Value.Bic : null));

            cancellationToken.ThrowIfCancellationRequested();

            if (candidate.IsSuccess && (best is null || candidate.Value.Bic < best.Bic)) best = candidate.Value;

            // Ascending K and one canonical initialized fit per K make exact ties retain the lower K.
        }

        return best is null ? new Error("Raptor.NoConvergedFit", "No candidate produced a converged finite fit.") : best;
    }

    internal Result<RaptorGmmFit> FitInitialized(double[][] points, double[] weights, double[][] means, double[][][] covariances, CancellationToken cancellationToken, int maximumIterations = 100)
    {
        var prepared = Prepare(points, cancellationToken);

        if (prepared.IsFailure) return prepared.Error;

        var initial = CreateParameters(weights, means, covariances, cancellationToken);

        if (initial.IsFailure) return initial.Error;

        if (means[0].Length != prepared.Value[0].Length || weights.Length > points.Length) return Invalid();

        return Iterate(prepared.Value, initial.Value, maximumIterations, cancellationToken);
    }

    // Analytic fixture seam: an immutable parameter snapshot, not a selected/converged training result.
    internal static Result<RaptorGmmFit> FromParameters(double[] weights, double[][] means, double[][][] covariances)
    {
        var parameters = CreateParameters(weights, means, covariances, default);

        return parameters.IsFailure ? parameters.Error : new RaptorGmmFit(parameters.Value, 0, 0, 0);
    }

    internal static double CalculateBic(double likelihood, int samples, int dimensions, int components) =>
        -2 * likelihood + ((components - 1d) + components * dimensions + components * dimensions * (dimensions + 1d) / 2) * Math.Log(samples);

    private Result<RaptorGmmFit> FitPrepared(double[][] points, int components, ulong seed, CancellationToken cancellationToken, int maximumIterations)
    {
        if (components < 1 || components >= Options.ComponentUpperBoundExclusive || components > points.Length) return Invalid();

        progress?.Report(new(components, 0, 0));

        cancellationToken.ThrowIfCancellationRequested();

        if (CompareRows(points[0], points[^1]) == 0)
        {
            if (components != 1) return Vanishing();

            var covariance = Matrix(points[0].Length);

            for (int d = 0; d < covariance.Length; d++) covariance[d][d] = Options.CovarianceRegularization;

            var parameters = CreateParameters([1], [points[0]], [covariance], cancellationToken);

            if (parameters.IsFailure) return parameters.Error;

            return Finish(points, parameters.Value, 0, cancellationToken);
        }

        var initial = Initialize(points, components, seed, cancellationToken);

        if (initial.IsFailure) return initial.Error;

        return Iterate(points, initial.Value, maximumIterations, cancellationToken);
    }

    private Result<RaptorGmmFit> Iterate(double[][] points, Parameters current, int maximumIterations, CancellationToken cancellationToken)
    {
        int components = current.Weights.Length;

        int dimensions = points[0].Length;

        double previousAverage = double.NegativeInfinity;

        double[] posteriors = new double[Options.MixtureBlockSize * components];

        for (int iteration = 1; iteration <= maximumIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var moments = new Moments(components, dimensions);

            double likelihood = 0;

            for (int start = 0; start < points.Length; start += Options.MixtureBlockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int end = Math.Min(points.Length, start + Options.MixtureBlockSize);

                for (int row = start; row < end; row++)
                {
                    Span<double> posterior = posteriors.AsSpan((row - start) * components, components);

                    if (!Posterior(current, points[row], posterior, cancellationToken, out double pointLikelihood)) return Invalid();

                    likelihood += pointLikelihood;
                }

                for (int row = start; row < end; row++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    for (int component = 0; component < components; component++) moments.Add(component, points[row], posteriors[(row - start) * components + component]);
                }

                progress?.Report(new(components, iteration, end));

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!double.IsFinite(likelihood)) return Invalid();

            var updated = moments.Parameters(cancellationToken);

            if (updated.IsFailure) return updated.Error;

            current = updated.Value;

            double average = likelihood / points.Length;

            // sklearn 1.4.2 tests the pre-M average change after performing M, then scores final parameters.
            if (Math.Abs(average - previousAverage) < Options.AverageLikelihoodTolerance) return Finish(points, current, iteration, cancellationToken);

            previousAverage = average;
        }

        return new Error("Raptor.Nonconverged", "The candidate did not converge within the iteration bound.");
    }

    private static Result<RaptorGmmFit> Finish(double[][] points, Parameters parameters, int iterations, CancellationToken cancellationToken)
    {
        int[] order = Enumerable.Range(0, parameters.Weights.Length).ToArray();

        Array.Sort(order, (left, right) => CompareComponents(parameters, left, right));

        var canonical = CreateParameters(order.Select(k => parameters.Weights[k]).ToArray(), order.Select(k => parameters.Means[k]).ToArray(), order.Select(k => parameters.Covariances[k]).ToArray(), cancellationToken);

        if (canonical.IsFailure) return canonical.Error;

        parameters = canonical.Value;

        double likelihood = 0;

        Span<double> posterior = stackalloc double[50];

        foreach (double[] point in points)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Posterior(parameters, point, posterior[..order.Length], cancellationToken, out double value)) return Invalid();

            likelihood += value;
        }

        double bic = CalculateBic(likelihood, points.Length, points[0].Length, order.Length);

        if (!double.IsFinite(likelihood) || !double.IsFinite(bic)) return Invalid();

        return new RaptorGmmFit(parameters, likelihood, bic, iterations);
    }

    internal static bool Posterior(Parameters parameters, ReadOnlySpan<double> point, Span<double> destination, CancellationToken cancellationToken, out double likelihood)
    {
        likelihood = 0;

        double maximum = double.NegativeInfinity;

        for (int k = 0; k < parameters.Weights.Length; k++)
        {
            destination[k] = Math.Log(parameters.Weights[k]) + RaptorSmallMatrix.LogDensity(point, parameters.Means[k], parameters.Lower[k], cancellationToken);

            if (!double.IsFinite(destination[k])) return false;

            maximum = Math.Max(maximum, destination[k]);
        }

        double sum = 0;

        for (int k = 0; k < destination.Length; k++)
        {
            destination[k] = Math.Exp(destination[k] - maximum);

            sum += destination[k];
        }

        likelihood = maximum + Math.Log(sum);

        if (!double.IsFinite(likelihood) || !double.IsFinite(sum) || sum <= 0) return false;

        double normalized = 0;

        for (int k = 0; k < destination.Length; k++)
        {
            destination[k] /= sum;

            normalized += destination[k];

            if (!double.IsFinite(destination[k]) || destination[k] < 0) return false;
        }

        return Math.Abs(normalized - 1) <= Options.MixtureNormalizationTolerance;
    }

    private static Result<Parameters> Initialize(double[][] points, int components, ulong seed, CancellationToken cancellationToken)
    {
        string digest = RaptorIdentityHash.OfParts(["raptor-gmm-kmeans-seed-v1", seed.ToString(CultureInfo.InvariantCulture), components.ToString(CultureInfo.InvariantCulture)]);

        var random = new TapestryDeterministicRandom(BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(digest)));

        double[][] centers = new double[components][];

        centers[0] = points[(int)(random.NextUInt64() % (ulong)points.Length)].ToArray();

        double[] nearest = Enumerable.Repeat(double.PositiveInfinity, points.Length).ToArray();

        for (int component = 1; component < components; component++)
        {
            double sum = 0;

            for (int row = 0; row < points.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                nearest[row] = Math.Min(nearest[row], Distance(points[row], centers[component - 1]));

                sum += nearest[row];
            }

            if (!double.IsFinite(sum)) return Invalid();

            if (sum <= 0) return Vanishing();

            double target = random.NextDouble() * sum;

            int selected = points.Length - 1;

            for (int row = 0; row < points.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (nearest[row] > 0 && target < nearest[row])
                {
                    selected = row;

                    break;
                }
                target -= nearest[row];
            }

            centers[component] = points[selected].ToArray();
        }

        int[] labels = Enumerable.Repeat(-1, points.Length).ToArray();

        for (int iteration = 0; iteration < Options.KMeansMaximumIterations; iteration++)
        {
            var moments = new Moments(components, points[0].Length);

            bool changed = false;

            for (int row = 0; row < points.Length; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int best = 0;

                double distance = Distance(points[row], centers[0]);

                for (int k = 1; k < components; k++)
                {
                    double candidate = Distance(points[row], centers[k]);

                    if (candidate < distance)
                    {
                        best = k;

                        distance = candidate;
                    }
                }

                if (!double.IsFinite(distance)) return Invalid();

                changed |= labels[row] != best;

                labels[row] = best;

                moments.Add(best, points[row], 1);
            }

            var parameters = moments.Parameters(cancellationToken);

            if (parameters.IsFailure) return parameters.Error;

            if (!changed || iteration == Options.KMeansMaximumIterations - 1) return parameters;

            centers = parameters.Value.Means;
        }

        return Invalid();
    }

    private static Result<double[][]> Prepare(double[][] points, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (points is null || points.Length == 0 || points[0] is null || points[0].Length < 1 || points[0].Length > Options.ReductionDimensions) return Invalid();

        double[][] sorted = new double[points.Length][];

        for (int index = 0; index < points.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (points[index] is null || points[index].Length != points[0].Length || points[index].Any(value => !double.IsFinite(value))) return Invalid();

            sorted[index] = points[index].ToArray();

            // Canonicalize signed zero: numerically identical rows must not gain order-dependent parameter bits.
            for (int d = 0; d < sorted[index].Length; d++) if (sorted[index][d] == 0) sorted[index][d] = 0;
        }

        RaptorFuzzyGraphBuilder.Sort(sorted, CompareRows, cancellationToken);

        return sorted;
    }

    private static Result<Parameters> CreateParameters(double[] weights, double[][] means, double[][][] covariances, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (weights is null || means is null || covariances is null || weights.Length < 1 || weights.Length >= Options.ComponentUpperBoundExclusive || means.Length != weights.Length || covariances.Length != weights.Length) return Invalid();

        if (means[0] is null || means[0].Length < 1 || means[0].Length > Options.ReductionDimensions) return Invalid();

        int dimensions = means[0].Length;

        double sum = 0;

        double[][][] lower = new double[weights.Length][][];

        for (int k = 0; k < weights.Length; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!double.IsFinite(weights[k]) || weights[k] <= 0 || means[k] is null || means[k].Length != dimensions || means[k].Any(v => !double.IsFinite(v)) || covariances[k] is null || covariances[k].Length != dimensions) return Invalid();

            sum += weights[k];

            var factor = RaptorSmallMatrix.Cholesky(covariances[k], cancellationToken);

            if (factor.IsFailure) return factor.Error;

            lower[k] = factor.Value;
        }

        if (!double.IsFinite(sum) || Math.Abs(sum - 1) > Options.MixtureNormalizationTolerance) return Invalid();

        return new Parameters(weights.ToArray(), means.Select(row => row.ToArray()).ToArray(), covariances.Select(m => m.Select(row => row.ToArray()).ToArray()).ToArray(), lower);
    }

    private static int CompareRows(double[] left, double[] right)
    {
        for (int d = 0; d < left.Length; d++)
        {
            int comparison = left[d].CompareTo(right[d]);

            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static int CompareComponents(Parameters parameters, int left, int right)
    {
        int comparison = CompareRows(parameters.Means[left], parameters.Means[right]);

        if (comparison != 0) return comparison;

        for (int row = 0; row < parameters.Means[0].Length; row++)
        {
            comparison = CompareRows(parameters.Covariances[left][row], parameters.Covariances[right][row]);

            if (comparison != 0) return comparison;
        }

        return parameters.Weights[left].CompareTo(parameters.Weights[right]);
    }

    private static double Distance(double[] left, double[] right)
    {
        double sum = 0;

        for (int d = 0; d < left.Length; d++)
        {
            double delta = left[d] - right[d];

            sum += delta * delta;
        }

        return sum;
    }

    private static double[][] Matrix(int dimensions) => Enumerable.Range(0, dimensions).Select(_ => new double[dimensions]).ToArray();

    private static Error Invalid() => new("Raptor.InvalidParameters", "Mixture input or arithmetic is invalid.");

    private static Error Vanishing() => new("Raptor.VanishingComponent", "A mixture component has insufficient effective mass.");

    internal sealed record Parameters(double[] Weights, double[][] Means, double[][][] Covariances, double[][][] Lower);

    private sealed class Moments(int components, int dimensions)
    {
        private readonly double[] _mass = new double[components];

        private readonly double[][] _means = Enumerable.Range(0, components).Select(_ => new double[dimensions]).ToArray();

        private readonly double[][][] _scatter = Enumerable.Range(0, components).Select(_ => Matrix(dimensions)).ToArray();

        internal void Add(int component, double[] point, double weight)
        {
            if (weight == 0) return;

            double next = _mass[component] + weight;

            double ratio = weight / next;

            Span<double> delta = stackalloc double[10];

            for (int d = 0; d < dimensions; d++)
            {
                delta[d] = point[d] - _means[component][d];

                _means[component][d] += ratio * delta[d];
            }

            // Weighted Welford: prior mass / next mass avoids cancellation in (point - updated mean).
            double scale = weight * (_mass[component] / next);

            for (int row = 0; row < dimensions; row++)
                for (int column = 0; column <= row; column++) _scatter[component][row][column] += scale * delta[row] * delta[column];

            _mass[component] = next;
        }

        internal Result<Parameters> Parameters(CancellationToken cancellationToken)
        {
            double total = _mass.Sum();

            if (!double.IsFinite(total) || total <= 0) return Invalid();

            double[] weights = new double[components];

            for (int k = 0; k < components; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_mass[k] <= Options.MinimumComponentMass) return Vanishing();

                weights[k] = _mass[k] / total;

                for (int row = 0; row < dimensions; row++)
                {
                    for (int column = 0; column <= row; column++)
                    {
                        double value = _scatter[k][row][column] / _mass[k] + (row == column ? Options.CovarianceRegularization : 0);

                        _scatter[k][row][column] = value;

                        _scatter[k][column][row] = value;
                    }
                }
            }

            return CreateParameters(weights, _means, _scatter, cancellationToken);
        }
    }
}
