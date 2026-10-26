using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>Original caller index and immutable source identity survive reduction, including isolated vertices.</summary>
public sealed record RaptorReducedPoint(int SourceIndex, string StableId, ImmutableArray<double> Coordinates);

/// <summary>Content-free bounded optimizer progress; observers do not participate in arithmetic.</summary>
public readonly record struct RaptorUmapProgress(int Epoch, long SampledEdges, long NegativeSamples);

/// <summary>
/// Double-precision Euclidean UMAP port from umap-learn 0.5.5 layouts.py and umap_.py.
/// Copyright (c) 2017 Leland McInnes. BSD-3-Clause; see UMAP-LICENSE.txt beside this source.
/// Canonical stable-id order and SplitMix64 replace upstream float32/Tausworthe, not its gradients.
/// </summary>
public sealed class RaptorUmapOptimizer(IProgress<RaptorUmapProgress>? progress = null)
{
    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    public Result<IReadOnlyList<RaptorReducedPoint>> Optimize(RaptorFuzzyGraph graph, int outputDimensions, ulong seed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (outputDimensions < 1 || outputDimensions > Options.ReductionDimensions) return new Error("Raptor.InvalidDimensions", "Output dimensions are outside the supported bounds.");

        if (!Valid(graph, cancellationToken)) return new Error("Raptor.InvalidGraph", "Fuzzy graph is inconsistent.");

        var curve = ResolveCurve(Options);

        if (curve.IsFailure) return curve.Error;

        int count = graph.PointCount;

        if ((long)count * outputDimensions > Array.MaxLength) return new Error("Raptor.TooLarge", "UMAP output exceeds managed array bounds.");

        int[] order = Enumerable.Range(0, count).ToArray();

        RaptorFuzzyGraphBuilder.Sort(order, (left, right) => StringComparer.Ordinal.Compare(graph.StableIds[left], graph.StableIds[right]), cancellationToken);

        int[] inverse = new int[count];

        for (int index = 0; index < count; index++) inverse[order[index]] = index;

        var random = new TapestryDeterministicRandom(seed);

        var coordinates = Initialize(count, outputDimensions, random, cancellationToken);

        int epochs = count <= Options.LargeEpochBoundary ? Options.SmallEpochs : Options.LargeEpochs;

        double maximum = 0;

        foreach (double weight in graph.Weights)
        {
            cancellationToken.ThrowIfCancellationRequested();

            maximum = Math.Max(maximum, weight);
        }

        var heads = new List<int>();

        var tails = new List<int>();

        var sampling = new List<double>();

        foreach (int original in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = new List<(int Tail, double Weight)>();

            for (int edge = graph.RowOffsets[original]; edge < graph.RowOffsets[original + 1]; edge++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double weight = graph.Weights[edge];

                if (weight >= maximum / epochs) row.Add((inverse[graph.SourceIndices[edge]], weight));
            }

            var orderedRow = row.ToArray();

            RaptorFuzzyGraphBuilder.Sort(orderedRow, (left, right) => left.Tail.CompareTo(right.Tail), cancellationToken);

            foreach (var edge in orderedRow)
            {
                cancellationToken.ThrowIfCancellationRequested();

                heads.Add(inverse[original]);

                tails.Add(edge.Tail);

                sampling.Add(maximum / edge.Weight);
            }
        }

        int[] head = heads.ToArray();

        int[] tail = tails.ToArray();

        double[] intervals = sampling.ToArray();

        double[] nextSample = (double[])intervals.Clone();

        double[] negativeIntervals = intervals.Select(value => value / Options.NegativeSampleRate).ToArray();

        double[] nextNegative = (double[])negativeIntervals.Clone();

        long sampled = 0;

        long negatives = 0;

        double alpha = Options.InitialLearningRate;

        Func<int, int, int> sampler = (_, _) => (int)(random.NextUInt64() % (ulong)count);

        for (int epoch = 0; epoch < epochs && head.Length > 0; epoch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int currentEpoch = epoch;

            Action<long, long>? observer = progress is null ? null : (edges, samples) => progress.Report(new(currentEpoch, sampled + edges, negatives + samples));

            var completed = RunEpoch(coordinates, head, tail, intervals, nextSample, negativeIntervals, nextNegative, epoch, alpha, curve.Value.A, curve.Value.B, Options.RepulsionStrength, sampler, cancellationToken, observer);

            sampled += completed.Edges;

            negatives += completed.Negatives;

            progress?.Report(new(epoch, sampled, negatives));

            cancellationToken.ThrowIfCancellationRequested();

            // Upstream updates after epoch n: epoch 1 still receives initial alpha.
            alpha = Options.InitialLearningRate * (1 - (double)epoch / epochs);
        }

        var result = new RaptorReducedPoint[count];

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (coordinates[index].Any(value => !double.IsFinite(value))) return new Error("Raptor.NonFinite", "UMAP did not produce finite coordinates.");

            int original = order[index];

            result[original] = new(original, graph.StableIds[original], [.. coordinates[index]]);
        }

        return Result<IReadOnlyList<RaptorReducedPoint>>.Success(Array.AsReadOnly(result));
    }

    /// <summary>
    /// Calibration lives in the independently pinned SciPy/UMAP fixture generator. This is the
    /// fixed .1/1 recipe, not a runtime nonlinear fitter; other curve recipes fail explicitly.
    /// </summary>
    internal static Result<(double A, double B)> ResolveCurve(RaptorNumericOptions options)
    {
        if (options.MinimumDistance != 0.1 || options.Spread != 1 || options.CurveA != 1.5769434603113077 || options.CurveB != 0.8950608779109733 || options.CurveRecipe != "umap-0.5.5-scipy-fit-spread1-mindist0.1-v1") return new Error("Raptor.UnsupportedCurve", "UMAP curve recipe is unsupported.");

        return (options.CurveA, options.CurveB);
    }

    internal static double[][] Initialize(int count, int dimensions, TapestryDeterministicRandom random, CancellationToken cancellationToken = default)
    {
        double[][] coordinates = new double[count][];

        for (int row = 0; row < count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            coordinates[row] = new double[dimensions];

            for (int dimension = 0; dimension < dimensions; dimension++) coordinates[row][dimension] = (2 * random.NextDouble() - 1) * Options.InitialCoordinateRange;
        }
        // Official random initialization is followed by per-axis [0,10] rescaling.
        for (int dimension = 0; dimension < dimensions; dimension++)
        {
            double minimum = double.PositiveInfinity;

            double maximum = double.NegativeInfinity;

            for (int row = 0; row < count; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                minimum = Math.Min(minimum, coordinates[row][dimension]);

                maximum = Math.Max(maximum, coordinates[row][dimension]);
            }

            for (int row = 0; row < count; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                coordinates[row][dimension] = maximum == minimum ? 0 : Options.InitialRescaleRange * (coordinates[row][dimension] - minimum) / (maximum - minimum);
            }
        }

        return coordinates;
    }

    /// <summary>Same epoch kernel used by production and independent fixed-coordinate/tape oracle tests.</summary>
    internal static (long Edges, long Negatives) RunEpoch(double[][] coordinates, int[] heads, int[] tails, double[] intervals, double[] nextSample, double[] negativeIntervals, double[] nextNegative, int epoch, double alpha, double a, double b, double gamma, Func<int, int, int> sample, CancellationToken cancellationToken, Action<long, long>? progress = null)
    {
        long edges = 0;

        long negatives = 0;

        for (int edge = 0; edge < heads.Length; edge++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (nextSample[edge] > epoch) continue;

            int vertex = heads[edge];

            double[] current = coordinates[vertex];

            double[] other = coordinates[tails[edge]];

            double squared = Squared(current, other);

            double coefficient = squared > 0 ? -2 * a * b * Math.Pow(squared, b - 1) / (a * Math.Pow(squared, b) + 1) : 0;

            for (int dimension = 0; dimension < current.Length; dimension++)
            {
                double gradient = Math.Clamp(coefficient * (current[dimension] - other[dimension]), -Options.GradientClip, Options.GradientClip);

                current[dimension] += gradient * alpha;

                other[dimension] -= gradient * alpha;
            }
            nextSample[edge] += intervals[edge];

            edges++;

            int samples = (int)((epoch - nextNegative[edge]) / negativeIntervals[edge]);

            for (int negative = 0; negative < samples; negative++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int target = sample(edge, negative);

                negatives++;

                other = coordinates[target];

                squared = Squared(current, other);

                coefficient = squared > 0 ? 2 * gamma * b / ((Options.RepulsionDistanceFloor + squared) * (a * Math.Pow(squared, b) + 1)) : 0;

                for (int dimension = 0; dimension < current.Length; dimension++)
                {
                    double gradient = coefficient > 0 ? Math.Clamp(coefficient * (current[dimension] - other[dimension]), -Options.GradientClip, Options.GradientClip) : 0;

                    current[dimension] += gradient * alpha;
                }

                if ((edges + negatives) % Options.UmapProgressInterval == 0) progress?.Invoke(edges, negatives);
            }
            nextNegative[edge] += samples * negativeIntervals[edge];

            if ((edges + negatives) % Options.UmapProgressInterval == 0) progress?.Invoke(edges, negatives);
        }
        cancellationToken.ThrowIfCancellationRequested();

        return (edges, negatives);
    }

    private static double Squared(double[] left, double[] right)
    {
        double squared = 0;

        for (int dimension = 0; dimension < left.Length; dimension++)
        {
            double delta = left[dimension] - right[dimension];

            squared += delta * delta;
        }

        return squared;
    }

    private static bool Valid(RaptorFuzzyGraph graph, CancellationToken cancellationToken)
    {
        if (graph is null || graph.StableIds.IsDefault || graph.PointCount >= Array.MaxLength || !RaptorFuzzyGraphBuilder.ValidIds(graph.StableIds, graph.PointCount, cancellationToken)) return false;

        int count = graph.PointCount;

        if (graph.RowOffsets.IsDefault || graph.SourceIndices.IsDefault || graph.Weights.IsDefault || graph.Rhos.IsDefault || graph.Sigmas.IsDefault || graph.RowOffsets.Length != count + 1 || graph.SourceIndices.Length != graph.Weights.Length || graph.Rhos.Length != count || graph.Sigmas.Length != count || graph.RowOffsets[0] != 0 || graph.RowOffsets[count] != graph.Weights.Length) return false;

        for (int row = 0; row < count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!double.IsFinite(graph.Rhos[row]) || graph.Rhos[row] < 0 || graph.Rhos[row] > 2 || !double.IsFinite(graph.Sigmas[row]) || graph.Sigmas[row] <= 0 || graph.RowOffsets[row] < 0 || graph.RowOffsets[row + 1] < graph.RowOffsets[row] || graph.RowOffsets[row + 1] > graph.Weights.Length) return false;

            int previous = -1;

            for (int edge = graph.RowOffsets[row]; edge < graph.RowOffsets[row + 1]; edge++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int column = graph.SourceIndices[edge];

                double weight = graph.Weights[edge];

                if (column <= previous || column >= count || column == row || !double.IsFinite(weight) || weight <= 0 || weight > 1) return false;

                previous = column;
            }
        }
        // CSR binary lookup validates symmetry without allocating dense probabilities.
        for (int row = 0; row < count; row++)
        {
            for (int edge = graph.RowOffsets[row]; edge < graph.RowOffsets[row + 1]; edge++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int other = graph.SourceIndices[edge];

                int start = graph.RowOffsets[other];

                int end = graph.RowOffsets[other + 1];

                int found = graph.SourceIndices.AsSpan(start, end - start).BinarySearch(row);

                if (found < 0 || graph.Weights[start + found] != graph.Weights[edge]) return false;
            }
        }

        return true;
    }
}
