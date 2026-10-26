using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Weave.Tapestry.Raptor;

/// <summary>Immutable symmetric CSR, sorted by original source index within each row, without self/zero edges.</summary>
public sealed record RaptorFuzzyGraph(
    ImmutableArray<string> StableIds,
    ImmutableArray<int> RowOffsets,
    ImmutableArray<int> SourceIndices,
    ImmutableArray<double> Weights,
    ImmutableArray<double> Rhos,
    ImmutableArray<double> Sigmas)
{
    public int PointCount => StableIds.Length;
}

/// <summary>
/// Double-precision port of umap-learn 0.5.5 smooth_knn_dist and membership/fuzzy-union equations.
/// Copyright (c) 2017 Leland McInnes. BSD-3-Clause; see UMAP-LICENSE.txt beside this source.
/// Includes self in k and the mean, excludes self from calibration's sum and from graph edges.
/// </summary>
public sealed class RaptorFuzzyGraphBuilder
{
    private static RaptorNumericOptions Options => RaptorAlgorithmManifest.Current.Options;

    public Result<RaptorFuzzyGraph> Build(RaptorNeighborGraph graph, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Valid(graph, cancellationToken)) return new Error("Raptor.InvalidGraph", "Neighbor graph is inconsistent.");

        int count = graph.PointCount;

        if (count == 0) return new RaptorFuzzyGraph([], [0], [], [], [], []);

        var distances = graph.Distances.Span;

        var indices = graph.SourceIndices.Span;

        double mean = 0;

        // Canonical row order also pins accumulation when input rows are permuted.
        int[] order = Enumerable.Range(0, count).ToArray();

        Sort(order, (left, right) => StringComparer.Ordinal.Compare(graph.StableIds[left], graph.StableIds[right]), cancellationToken);

        foreach (int row in order)
        {
            for (int column = 0; column < graph.Neighbors; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                mean += distances[row * graph.Neighbors + column] / distances.Length;
            }
        }

        double[] rhos = new double[count];

        double[] sigmas = new double[count];

        var union = new Dictionary<ulong, double>();

        foreach (int row in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var local = distances.Slice(row * graph.Neighbors, graph.Neighbors);

            (double rho, double sigma) = Smooth(local, mean, cancellationToken);

            rhos[row] = rho;

            sigmas[row] = sigma;

            for (int column = 1; column < graph.Neighbors; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double distance = local[column] - rho;

                double weight = distance <= 0 ? 1 : Math.Exp(-distance / sigma);

                // Underflow is a mathematically negligible edge, never a dropped vertex.
                if (weight == 0) continue;

                int other = indices[row * graph.Neighbors + column];

                ulong key = ((ulong)(uint)Math.Min(row, other) << 32) | (uint)Math.Max(row, other);

                union.TryGetValue(key, out double reverse);

                union[key] = weight + reverse - weight * reverse;
            }
        }

        if (2L * union.Count > Array.MaxLength) return new Error("Raptor.TooLarge", "Fuzzy graph exceeds managed array bounds.");

        int[] offsets = new int[count + 1];

        foreach (ulong key in union.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            offsets[(int)(key >> 32) + 1]++;

            offsets[(int)(uint)key + 1]++;
        }

        for (int row = 1; row <= count; row++) offsets[row] += offsets[row - 1];

        int[] cursor = (int[])offsets.Clone();

        int[] columns = new int[2 * union.Count];

        double[] weights = new double[columns.Length];

        foreach ((ulong key, double weight) in union)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int left = (int)(key >> 32);

            int right = (int)(uint)key;

            int forward = cursor[left]++;

            int reverse = cursor[right]++;

            columns[forward] = right;

            columns[reverse] = left;

            weights[forward] = weights[reverse] = weight;
        }

        var comparer = Comparer<int>.Create((left, right) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            return left.CompareTo(right);
        });

        for (int row = 0; row < count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Array.Sort(columns, weights, offsets[row], offsets[row + 1] - offsets[row], comparer);
            }
            catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return new RaptorFuzzyGraph(graph.StableIds, [.. offsets], [.. columns], [.. weights], [.. rhos], [.. sigmas]);
    }

    private static (double Rho, double Sigma) Smooth(ReadOnlySpan<double> distances, double mean, CancellationToken cancellationToken)
    {
        double rho = 0;

        double localMean = 0;

        int nonzero = 0;

        int connectivity = (int)Math.Floor(Options.LocalConnectivity);

        double interpolation = Options.LocalConnectivity - connectivity;

        foreach (double distance in distances)
        {
            cancellationToken.ThrowIfCancellationRequested();

            localMean += distance / distances.Length;

            if (distance <= 0) continue;

            nonzero++;

            if (nonzero <= connectivity) rho = distance;
            else if (nonzero == connectivity + 1 && interpolation > Options.SmoothTolerance) rho += interpolation * (distance - rho);
        }

        double target = Math.Log2(distances.Length) * Options.SmoothBandwidth;

        double low = 0;

        double high = double.PositiveInfinity;

        double sigma = 1;

        for (int iteration = 0; iteration < Options.SmoothIterations; iteration++)
        {
            double sum = 0;

            for (int column = 1; column < distances.Length; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                double distance = distances[column] - rho;

                sum += distance > 0 ? Math.Exp(-distance / sigma) : 1;
            }

            if (Math.Abs(sum - target) < Options.SmoothTolerance) break;

            if (sum > target)
            {
                high = sigma;

                sigma = (low + high) / 2;
            }
            else
            {
                low = sigma;

                sigma = double.IsPositiveInfinity(high) ? sigma * 2 : (low + high) / 2;
            }
        }
        // All-identical distances retain the positive binary-search endpoint (2^-64).
        return (rho, Math.Max(sigma, Options.MinimumSigmaScale * (rho > 0 ? localMean : mean)));
    }

    internal static void Sort<T>(T[] values, Comparison<T> comparison, CancellationToken cancellationToken)
    {
        try
        {
            Array.Sort(values, (left, right) =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                return comparison(left, right);
            });
        }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException)
        {
            // Array.Sort wraps comparer exceptions; preserve the numeric cancellation contract.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    internal static bool ValidIds(ImmutableArray<string> ids, int count, CancellationToken cancellationToken)
    {
        if (ids.IsDefault || ids.Length != count) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) return false;

            for (int index = 0; index < id.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (char.IsHighSurrogate(id[index]))
                {
                    if (++index == id.Length || !char.IsLowSurrogate(id[index])) return false;
                }
                else if (char.IsLowSurrogate(id[index])) return false;
            }
        }

        return true;
    }

    private static bool Valid(RaptorNeighborGraph graph, CancellationToken cancellationToken)
    {
        if (graph is null || graph.PointCount < 0 || graph.PointCount >= Array.MaxLength || !ValidIds(graph.StableIds, graph.PointCount, cancellationToken)) return false;

        int count = graph.PointCount;

        int neighbors = graph.Neighbors;

        if ((count == 0 && neighbors != 0) || (count > 0 && (neighbors < 1 || neighbors > count))) return false;

        if ((long)count * neighbors != graph.SourceIndices.Length || graph.Distances.Length != graph.SourceIndices.Length || graph.RowOffsets.Length != count + 1) return false;

        var offsets = graph.RowOffsets.Span;

        var indices = graph.SourceIndices.Span;

        var distances = graph.Distances.Span;

        var seen = new HashSet<int>();

        for (int row = 0; row <= count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (offsets[row] != (long)row * neighbors) return false;

            if (row == count) break;

            seen.Clear();

            double previous = 0;

            for (int column = 0; column < neighbors; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int edge = offsets[row] + column;

                double distance = distances[edge];

                if (indices[edge] < 0 || indices[edge] >= count || !seen.Add(indices[edge]) || !double.IsFinite(distance) || distance < previous || distance > 2) return false;

                if (column == 0 && (indices[edge] != row || distance != 0)) return false;

                previous = distance;
            }
        }

        return true;
    }
}
