namespace RetroDownfall.Arcanum.Core.Weave.Tapestry;

/// <summary>Why a usable-vector check rejected a node from a Tapestry layer.</summary>
public enum SphericalKMeansRejection
{

    /// <summary>At least one component was <c>NaN</c> or infinite.</summary>
    NonFiniteComponent,

    /// <summary>The vector's L2 norm was zero (or denormal-flushed to zero), so it has no direction.</summary>
    ZeroNorm,

    /// <summary>The vector's length differed from the layer's single validated dimension.</summary>
    DimensionMismatch,

}

/// <summary>Which stopping condition ended a clustering run. Recorded with every generation.</summary>
public enum SphericalKMeansTermination
{

    /// <summary>
    /// No point changed cluster between two consecutive assignment passes. Empty-cluster repair runs
    /// after the assignment pass, so the published memberships are always repaired even when the
    /// assignment step itself reported no change.
    /// </summary>
    AssignmentsStable,

    /// <summary>Every centroid moved less than the convergence tolerance.</summary>
    ToleranceReached,

    /// <summary>The code-owned iteration cap was reached first.</summary>
    IterationCap,

}

/// <summary>One clustering input: a stable opaque identity plus its raw (un-normalized) vector.</summary>
public sealed record SphericalKMeansPoint(string StableId, float[] Vector);

/// <summary>A node excluded from clustering, retained for sanitized diagnostics.</summary>
public sealed record SphericalKMeansRejectedPoint(string StableId, SphericalKMeansRejection Reason);

/// <summary>
/// One cluster. <see cref="MemberIds"/> is sorted ordinally, and <see cref="Ordinal"/> follows the
/// clusters' own ordering rule (lowest member id first), so both are reproducible.
/// </summary>
public sealed record SphericalKMeansCluster(
    int Ordinal,
    IReadOnlyList<string> MemberIds,
    float[] Centroid);

/// <summary>The complete, reproducible outcome of one clustering run.</summary>
public sealed record SphericalKMeansResult(
    IReadOnlyList<SphericalKMeansCluster> Clusters,
    int Iterations,
    SphericalKMeansTermination Termination,
    IReadOnlyList<SphericalKMeansRejectedPoint> Rejected);

/// <summary>
/// The Tapestry's clustering step (DESIGN §21.11): a native, dependency-free <b>spherical K-Means</b>.
///
/// <para><b>This is an intentional RAPTOR variant, not the paper's algorithm.</b> RAPTOR (Sarthi et
/// al., arXiv 2401.18059) reduces dimensionality with UMAP and soft-clusters with a Gaussian Mixture
/// Model. Both are Python/native libraries with no Native-AOT-safe managed equivalent, and GMM's soft
/// membership would let one leaf influence several branches — which makes provenance and deletion
/// intractable. Spherical K-Means with hard assignment is used by practical RAPTOR recipes and is what
/// Arcanum implements; nothing here claims algorithmic equivalence with the paper.</para>
///
/// <para><b>Metric.</b> Every usable vector is L2-normalized, so cosine similarity equals the dot
/// product and centroids are re-normalized after each update — consistent with Divination's cosine
/// metric (§21.2). Ordinary unnormalized Euclidean K-Means is deliberately not used.</para>
///
/// <para><b>Reproducibility contract.</b> Given the same persisted vector bytes, the same
/// <see cref="AlgorithmVersion"/>, the same settings, and the same runtime numeric contract, this
/// produces the same memberships, the same cluster ordering, and the same stable ids. It does
/// <b>not</b> promise bit-for-bit identical centroid components across every CPU or architecture —
/// no test establishes that, so it is not claimed. Accumulation is single-threaded in stable member
/// order (never a nondeterministic parallel floating-point reduction), which is what makes the
/// membership guarantee hold.</para>
/// </summary>
public static class SphericalKMeans
{

    /// <summary>
    /// The versioned clustering-algorithm identity. Persisted with every generation; changing it
    /// invalidates derived generations and forces an explicit rebuild.
    /// </summary>
    public const string AlgorithmVersion = "spherical-kmeans-hard-v1";

    /// <summary>
    /// Code-owned iteration cap. This bounds <b>one</b> clustering computation, not the generation
    /// builder's total work — the builder continues checkpointed work until the tree is complete or
    /// cancelled (§21.11, #55).
    /// </summary>
    public const int MaxIterations = 100;

    /// <summary>
    /// Convergence tolerance on the largest centroid movement (as cosine distance) between two
    /// iterations.
    /// </summary>
    public const double ConvergenceTolerance = 1e-6;

    /// <summary>
    /// Derives <c>k</c> for a layer deterministically from the target children-per-summary rather
    /// than exposing an arbitrary fixed cluster count: <c>ceil(n / target)</c>, clamped to
    /// <c>2..min(n, distinctVectorCount, maxClustersPerLayer)</c>. Degenerate layers (a single node,
    /// or a single distinct direction) collapse to <c>1</c>; an empty layer yields <c>0</c>.
    /// </summary>
    public static int DeriveClusterCount(
        int nodeCount,
        int targetChildrenPerSummary,
        int maxClustersPerLayer,
        int distinctVectorCount)
    {

        if (nodeCount <= 0 || distinctVectorCount <= 0)
        {

            return 0;

        }

        int target = Math.Max(1, targetChildrenPerSummary);

        int ceiling = (nodeCount + target - 1) / target;

        int upperBound = Math.Min(
            nodeCount,
            Math.Min(Math.Max(1, distinctVectorCount), Math.Max(1, maxClustersPerLayer)));

        return upperBound <= 1
            ? 1
            : Math.Clamp(ceiling, 2, upperBound);

    }

    /// <summary>
    /// Clusters <paramref name="points"/> into at most <paramref name="k"/> clusters.
    ///
    /// <paramref name="expectedDimensions"/> pins the layer's single validated dimension; when
    /// <c>null</c> it is inferred from the first usable vector in stable-id order (deterministic, and
    /// independent of the caller's enumeration order). Non-finite, zero-norm, and
    /// dimension-mismatched vectors are quarantined into
    /// <see cref="SphericalKMeansResult.Rejected"/> and never truncated, padded, or clustered.
    /// </summary>
    public static SphericalKMeansResult Cluster(
        IReadOnlyList<SphericalKMeansPoint> points,
        int k,
        ulong seed,
        int? expectedDimensions = null,
        int maxIterations = MaxIterations)
    {

        ArgumentNullException.ThrowIfNull(points);

        Prepared prepared = Prepare(points, expectedDimensions);

        return ClusterPrepared(prepared, k, seed, maxIterations);

    }

    /// <summary>
    /// Clusters one Tapestry layer, deriving <c>k</c> from the target children-per-summary rather than
    /// taking it from the caller. The distinct-direction count is measured on the layer's own usable
    /// vectors, so a degenerate layer collapses instead of asking for more centroids than the data can
    /// support.
    /// </summary>
    public static SphericalKMeansResult ClusterLayer(
        IReadOnlyList<SphericalKMeansPoint> points,
        int targetChildrenPerSummary,
        int maxClustersPerLayer,
        ulong seed,
        int? expectedDimensions = null,
        int maxIterations = MaxIterations)
    {

        ArgumentNullException.ThrowIfNull(points);

        Prepared prepared = Prepare(points, expectedDimensions);

        int k = DeriveClusterCount(
            prepared.Ids.Count,
            targetChildrenPerSummary,
            maxClustersPerLayer,
            prepared.DistinctVectors);

        return ClusterPrepared(prepared, k, seed, maxIterations);

    }

    private sealed record Prepared(
        List<string> Ids,
        List<float[]> Vectors,
        List<SphericalKMeansRejectedPoint> Rejected,
        int DistinctVectors);

    private static Prepared Prepare(
        IReadOnlyList<SphericalKMeansPoint> points,
        int? expectedDimensions)
    {

        // Stable input order first: every downstream decision (dimension inference, K-Means++
        // selection, assignment tie-breaks, centroid accumulation order) is defined relative to this
        // ordering, so the caller's enumeration order can never change the outcome.
        SphericalKMeansPoint[] ordered = [.. points.OrderBy(static point => point.StableId, StringComparer.Ordinal)];

        List<SphericalKMeansRejectedPoint> rejected = [];

        int dimensions = expectedDimensions ?? InferDimensions(ordered);

        List<string> usableIds = [];

        List<float[]> usableVectors = [];

        foreach (SphericalKMeansPoint point in ordered)
        {

            if (point.Vector is not { Length: > 0 } vector || vector.Length != dimensions)
            {

                rejected.Add(new SphericalKMeansRejectedPoint(
                    point.StableId,
                    SphericalKMeansRejection.DimensionMismatch));

                continue;

            }

            if (!TryNormalize(vector, out float[] normalized, out SphericalKMeansRejection reason))
            {

                rejected.Add(new SphericalKMeansRejectedPoint(point.StableId, reason));

                continue;

            }

            usableIds.Add(point.StableId);

            usableVectors.Add(normalized);

        }

        return new Prepared(
            usableIds,
            usableVectors,
            rejected,
            CountDistinctVectors(usableVectors));

    }

    private static SphericalKMeansResult ClusterPrepared(
        Prepared prepared,
        int k,
        ulong seed,
        int maxIterations)
    {

        List<string> usableIds = prepared.Ids;

        List<float[]> usableVectors = prepared.Vectors;

        List<SphericalKMeansRejectedPoint> rejected = prepared.Rejected;

        if (usableIds.Count == 0)
        {

            return new SphericalKMeansResult(
                [],
                0,
                SphericalKMeansTermination.AssignmentsStable,
                rejected);

        }

        int clusterCount = Math.Clamp(k, 1, Math.Min(usableIds.Count, prepared.DistinctVectors));

        float[][] centroids = InitializeCentroids(usableVectors, clusterCount, seed);

        int[] assignments = new int[usableIds.Count];

        Array.Fill(assignments, -1);

        int iterations = 0;

        SphericalKMeansTermination termination = SphericalKMeansTermination.IterationCap;

        int cap = Math.Max(1, maxIterations);

        for (; iterations < cap;)
        {

            iterations++;

            bool changed = Assign(usableVectors, centroids, assignments);

            RepairEmptyClusters(usableVectors, centroids, assignments);

            double shift = UpdateCentroids(usableVectors, assignments, centroids);

            if (!changed)
            {

                termination = SphericalKMeansTermination.AssignmentsStable;

                break;

            }

            if (shift < ConvergenceTolerance)
            {

                termination = SphericalKMeansTermination.ToleranceReached;

                break;

            }

        }

        return new SphericalKMeansResult(
            BuildClusters(usableIds, assignments, centroids),
            iterations,
            termination,
            rejected);

    }

    private static int InferDimensions(IReadOnlyList<SphericalKMeansPoint> ordered)
    {

        foreach (SphericalKMeansPoint point in ordered)
        {

            if (point.Vector is { Length: > 0 } vector
                && TryNormalize(vector, out _, out _))
            {

                return vector.Length;

            }

        }

        return ordered.Count > 0 ? Math.Max(1, ordered[0].Vector?.Length ?? 1) : 1;

    }

    private static bool TryNormalize(
        float[] vector,
        out float[] normalized,
        out SphericalKMeansRejection reason)
    {

        double sumOfSquares = 0;

        foreach (float component in vector)
        {

            if (!float.IsFinite(component))
            {

                normalized = [];

                reason = SphericalKMeansRejection.NonFiniteComponent;

                return false;

            }

            sumOfSquares += (double)component * component;

        }

        double norm = Math.Sqrt(sumOfSquares);

        if (norm <= 0 || !double.IsFinite(norm))
        {

            normalized = [];

            reason = SphericalKMeansRejection.ZeroNorm;

            return false;

        }

        normalized = new float[vector.Length];

        for (int index = 0; index < vector.Length; index++)
        {

            normalized[index] = (float)(vector[index] / norm);

        }

        reason = default;

        return true;

    }

    /// <summary>
    /// Counts <b>exactly</b> distinct normalized directions so <c>k</c> can never exceed the number
    /// of centroids the data can actually support — an identical-vector corpus must terminate rather
    /// than loop re-seeding duplicate centroids.
    ///
    /// Exact component equality is deliberate rather than a similarity threshold: a tolerance would
    /// silently collapse genuinely distinct near-neighbours (a codebase full of near-identical
    /// boilerplate is the common case) and hand back far fewer clusters than the layer asked for.
    /// The only thing this needs to prevent is duplicate centroids, and only exact duplicates can
    /// cause that.
    /// </summary>
    private static int CountDistinctVectors(IReadOnlyList<float[]> vectors)
    {

        HashSet<VectorKey> distinct = [];

        foreach (float[] vector in vectors)
        {

            _ = distinct.Add(new VectorKey(vector));

        }

        return distinct.Count;

    }

    private static bool AreEquivalent(float[] left, float[] right) =>
        left.AsSpan().SequenceEqual(right);

    /// <summary>
    /// Hashable identity for an exact normalized vector, so distinct-direction counting is O(n·d)
    /// instead of O(n²·d) on large layers.
    /// </summary>
    private readonly struct VectorKey(float[] vector) : IEquatable<VectorKey>
    {

        private readonly float[] _vector = vector;

        public bool Equals(VectorKey other) => _vector.AsSpan().SequenceEqual(other._vector);

        public override bool Equals(object? obj) => obj is VectorKey other && Equals(other);

        public override int GetHashCode()
        {

            HashCode hash = new();

            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_vector.AsSpan()));

            return hash.ToHashCode();

        }

    }

    /// <summary>
    /// Seeded K-Means++: the first centroid is drawn uniformly, each subsequent centroid with
    /// probability proportional to its squared Euclidean distance from the nearest chosen centroid
    /// (on unit vectors that is <c>2 - 2·cos</c>). All weighted-selection boundary cases fall back to
    /// the lowest-index (therefore lowest stable id) candidate that is not already a centroid.
    /// </summary>
    private static float[][] InitializeCentroids(
        IReadOnlyList<float[]> vectors,
        int clusterCount,
        ulong seed)
    {

        TapestryDeterministicRandom random = new(seed);

        float[][] centroids = new float[clusterCount][];

        int firstIndex = (int)Math.Min(
            vectors.Count - 1,
            (long)(random.NextDouble() * vectors.Count));

        centroids[0] = vectors[firstIndex];

        double[] squaredDistances = new double[vectors.Count];

        for (int chosen = 1; chosen < clusterCount; chosen++)
        {

            double total = 0;

            for (int index = 0; index < vectors.Count; index++)
            {

                double nearest = double.MaxValue;

                for (int centroid = 0; centroid < chosen; centroid++)
                {

                    double distance = Math.Max(0, 2.0 - (2.0 * Dot(vectors[index], centroids[centroid])));

                    if (distance < nearest)
                    {

                        nearest = distance;

                    }

                }

                squaredDistances[index] = nearest;

                total += nearest;

            }

            centroids[chosen] = total <= 0
                ? FirstUnusedVector(vectors, centroids, chosen)
                : SelectWeighted(vectors, squaredDistances, total, random.NextDouble());

        }

        return centroids;

    }

    private static float[] SelectWeighted(
        IReadOnlyList<float[]> vectors,
        double[] weights,
        double total,
        double sample)
    {

        double target = sample * total;

        double cumulative = 0;

        for (int index = 0; index < vectors.Count; index++)
        {

            cumulative += weights[index];

            if (cumulative > target)
            {

                return vectors[index];

            }

        }

        // Floating-point accumulation can leave `cumulative` a hair below `target`; falling back to
        // the last positive-weight candidate keeps the choice deterministic instead of throwing.
        for (int index = vectors.Count - 1; index >= 0; index--)
        {

            if (weights[index] > 0)
            {

                return vectors[index];

            }

        }

        return vectors[0];

    }

    private static float[] FirstUnusedVector(
        IReadOnlyList<float[]> vectors,
        float[][] centroids,
        int chosen)
    {

        foreach (float[] vector in vectors)
        {

            bool duplicate = false;

            for (int index = 0; index < chosen; index++)
            {

                if (AreEquivalent(centroids[index], vector))
                {

                    duplicate = true;

                    break;

                }

            }

            if (!duplicate)
            {

                return vector;

            }

        }

        return vectors[0];

    }

    /// <summary>
    /// Assigns every point to its most similar centroid. Ties break toward the lowest centroid
    /// ordinal, and points are visited in stable-id order, so the assignment is a pure function of
    /// the inputs.
    /// </summary>
    private static bool Assign(
        IReadOnlyList<float[]> vectors,
        float[][] centroids,
        int[] assignments)
    {

        bool changed = false;

        for (int index = 0; index < vectors.Count; index++)
        {

            int best = 0;

            double bestSimilarity = double.NegativeInfinity;

            for (int centroid = 0; centroid < centroids.Length; centroid++)
            {

                double similarity = Dot(vectors[index], centroids[centroid]);

                if (similarity > bestSimilarity)
                {

                    bestSimilarity = similarity;

                    best = centroid;

                }

            }

            if (assignments[index] != best)
            {

                assignments[index] = best;

                changed = true;

            }

        }

        return changed;

    }

    /// <summary>
    /// Deterministic empty-cluster repair: reseed from the point furthest from its assigned centroid
    /// (lowest similarity), breaking ties toward the lowest stable id — which, because
    /// <c>vectors</c> is already in stable-id order, is the lowest index. A donor cluster is never
    /// emptied to fill another.
    /// </summary>
    private static void RepairEmptyClusters(
        IReadOnlyList<float[]> vectors,
        float[][] centroids,
        int[] assignments)
    {

        int[] sizes = new int[centroids.Length];

        foreach (int assignment in assignments)
        {

            sizes[assignment]++;

        }

        for (int centroid = 0; centroid < centroids.Length; centroid++)
        {

            if (sizes[centroid] > 0)
            {

                continue;

            }

            int donor = -1;

            double worstSimilarity = double.MaxValue;

            for (int index = 0; index < vectors.Count; index++)
            {

                if (sizes[assignments[index]] <= 1)
                {

                    continue;

                }

                double similarity = Dot(vectors[index], centroids[assignments[index]]);

                if (similarity < worstSimilarity)
                {

                    worstSimilarity = similarity;

                    donor = index;

                }

            }

            if (donor < 0)
            {

                continue;

            }

            sizes[assignments[donor]]--;

            assignments[donor] = centroid;

            sizes[centroid]++;

            centroids[centroid] = vectors[donor];

        }

    }

    /// <summary>
    /// Recomputes every centroid as the L2-normalized mean of its members, accumulated in
    /// <c>double</c> in stable member order — never a parallel reduction, whose summation order (and
    /// therefore rounding) is not reproducible. Returns the largest centroid movement expressed as
    /// cosine distance.
    /// </summary>
    private static double UpdateCentroids(
        IReadOnlyList<float[]> vectors,
        int[] assignments,
        float[][] centroids)
    {

        int dimensions = centroids[0].Length;

        double maximumShift = 0;

        for (int centroid = 0; centroid < centroids.Length; centroid++)
        {

            double[] accumulator = new double[dimensions];

            int members = 0;

            for (int index = 0; index < vectors.Count; index++)
            {

                if (assignments[index] != centroid)
                {

                    continue;

                }

                float[] vector = vectors[index];

                for (int component = 0; component < dimensions; component++)
                {

                    accumulator[component] += vector[component];

                }

                members++;

            }

            if (members == 0)
            {

                continue;

            }

            double norm = 0;

            foreach (double component in accumulator)
            {

                norm += component * component;

            }

            norm = Math.Sqrt(norm);

            if (norm <= 0 || !double.IsFinite(norm))
            {

                continue;

            }

            float[] updated = new float[dimensions];

            for (int component = 0; component < dimensions; component++)
            {

                updated[component] = (float)(accumulator[component] / norm);

            }

            double shift = Math.Max(0, 1.0 - Dot(centroids[centroid], updated));

            if (shift > maximumShift)
            {

                maximumShift = shift;

            }

            centroids[centroid] = updated;

        }

        return maximumShift;

    }

    /// <summary>
    /// Projects assignments into clusters ordered by their lowest member id, with members sorted
    /// ordinally. Empty clusters are dropped — a layer never carries a summary node with no children.
    /// </summary>
    private static SphericalKMeansCluster[] BuildClusters(
        IReadOnlyList<string> ids,
        int[] assignments,
        float[][] centroids)
    {

        List<string>[] members = new List<string>[centroids.Length];

        for (int index = 0; index < members.Length; index++)
        {

            members[index] = [];

        }

        for (int index = 0; index < ids.Count; index++)
        {

            members[assignments[index]].Add(ids[index]);

        }

        List<SphericalKMeansCluster> clusters = [];

        for (int index = 0; index < members.Length; index++)
        {

            if (members[index].Count == 0)
            {

                continue;

            }

            members[index].Sort(StringComparer.Ordinal);

            clusters.Add(new SphericalKMeansCluster(0, members[index], centroids[index]));

        }

        clusters.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.MemberIds[0], right.MemberIds[0]));

        SphericalKMeansCluster[] result = new SphericalKMeansCluster[clusters.Count];

        for (int index = 0; index < clusters.Count; index++)
        {

            result[index] = clusters[index] with { Ordinal = index };

        }

        return result;

    }

    private static double Dot(float[] left, float[] right)
    {

        double sum = 0;

        int length = Math.Min(left.Length, right.Length);

        for (int index = 0; index < length; index++)
        {

            sum += (double)left[index] * right[index];

        }

        return sum;

    }

}
