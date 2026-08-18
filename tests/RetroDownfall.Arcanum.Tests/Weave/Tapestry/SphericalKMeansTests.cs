using System.Security.Cryptography;
using System.Text;

using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// The Tapestry's clustering contract (DESIGN §21.11): deterministic, pure-managed spherical
/// K-Means. These tests assert <b>memberships, ordering, and termination</b> — never exact
/// floating-point centroid components, which the documented reproducibility contract deliberately
/// does not promise across architectures.
/// </summary>
public sealed class SphericalKMeansTests
{

    private static SphericalKMeansPoint Point(string id, params float[] vector) => new(id, vector);

    [Fact]
    public void Cluster_SeparatesTwoObviousGroups()
    {

        SphericalKMeansPoint[] points =
        [
            Point("a1", 1f, 0f),
            Point("a2", 0.99f, 0.01f),
            Point("b1", 0f, 1f),
            Point("b2", 0.01f, 0.99f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 12345UL);

        Assert.Equal(2, result.Clusters.Count);

        Assert.Empty(result.Rejected);

        string[] first = [.. result.Clusters[0].MemberIds];

        string[] second = [.. result.Clusters[1].MemberIds];

        Assert.Equal(["a1", "a2"], first);

        Assert.Equal(["b1", "b2"], second);

    }

    [Fact]
    public void Cluster_NormalizesVectors_SoMagnitudeDoesNotChangeMembership()
    {

        SphericalKMeansPoint[] unit =
        [
            Point("a1", 1f, 0f),
            Point("a2", 0.99f, 0.01f),
            Point("b1", 0f, 1f),
            Point("b2", 0.01f, 0.99f),
        ];

        SphericalKMeansPoint[] scaled =
        [
            Point("a1", 40f, 0f),
            Point("a2", 3.96f, 0.04f),
            Point("b1", 0f, 900f),
            Point("b2", 0.05f, 4.95f),
        ];

        SphericalKMeansResult unitResult = SphericalKMeans.Cluster(unit, k: 2, seed: 7UL);

        SphericalKMeansResult scaledResult = SphericalKMeans.Cluster(scaled, k: 2, seed: 7UL);

        Assert.Equal(
            unitResult.Clusters.Select(static cluster => cluster.MemberIds),
            scaledResult.Clusters.Select(static cluster => cluster.MemberIds));

    }

    [Fact]
    public void Cluster_IsDeterministicForTheSameSeedAndInput()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 40).Select(index =>
                Point(
                    $"n{index:D3}",
                    MathF.Cos(index * 0.37f),
                    MathF.Sin(index * 0.37f),
                    MathF.Cos(index * 0.11f))),
        ];

        SphericalKMeansResult first = SphericalKMeans.Cluster(points, k: 5, seed: 99UL);

        SphericalKMeansResult second = SphericalKMeans.Cluster(points, k: 5, seed: 99UL);

        Assert.Equal(
            first.Clusters.Select(static cluster => (cluster.Ordinal, cluster.MemberIds)),
            second.Clusters.Select(static cluster => (cluster.Ordinal, cluster.MemberIds)));

        Assert.Equal(first.Termination, second.Termination);

        Assert.Equal(first.Iterations, second.Iterations);

    }

    [Fact]
    public void Cluster_InputOrderDoesNotChangeMemberships()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 24).Select(index =>
                Point(
                    $"n{index:D3}",
                    MathF.Cos(index * 0.51f),
                    MathF.Sin(index * 0.51f))),
        ];

        SphericalKMeansPoint[] shuffled = [.. points.Reverse()];

        SphericalKMeansResult ordered = SphericalKMeans.Cluster(points, k: 4, seed: 3UL);

        SphericalKMeansResult reversed = SphericalKMeans.Cluster(shuffled, k: 4, seed: 3UL);

        Assert.Equal(
            ordered.Clusters.Select(static cluster => cluster.MemberIds),
            reversed.Clusters.Select(static cluster => cluster.MemberIds));

    }

    [Fact]
    public void Cluster_OrdersClustersByLowestStableMemberId()
    {

        SphericalKMeansPoint[] points =
        [
            Point("zeta", 0f, 1f),
            Point("alpha", 1f, 0f),
            Point("beta", 0.98f, 0.02f),
            Point("yankee", 0.02f, 0.98f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 41UL);

        Assert.Equal(["alpha", "beta"], result.Clusters[0].MemberIds);

        Assert.Equal(["yankee", "zeta"], result.Clusters[1].MemberIds);

        Assert.Equal(0, result.Clusters[0].Ordinal);

        Assert.Equal(1, result.Clusters[1].Ordinal);

    }

    [Fact]
    public void Cluster_ClampsKToUsablePointCount()
    {

        SphericalKMeansPoint[] points =
        [
            Point("a", 1f, 0f),
            Point("b", 0f, 1f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 9, seed: 1UL);

        Assert.Equal(2, result.Clusters.Count);

    }

    [Fact]
    public void Cluster_ClampsKToDistinctVectorCount_AndTerminates()
    {

        SphericalKMeansPoint[] points =
        [
            Point("a", 1f, 0f),
            Point("b", 1f, 0f),
            Point("c", 1f, 0f),
            Point("d", 0f, 1f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 4, seed: 5UL);

        Assert.Equal(2, result.Clusters.Count);

        Assert.All(result.Clusters, static cluster => Assert.NotEmpty(cluster.MemberIds));

        Assert.Equal(4, result.Clusters.Sum(static cluster => cluster.MemberIds.Count));

    }

    [Fact]
    public void Cluster_IdenticalVectorCorpusProducesOneCluster()
    {

        SphericalKMeansPoint[] points =
        [
            Point("a", 0.5f, 0.5f),
            Point("b", 0.5f, 0.5f),
            Point("c", 0.5f, 0.5f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 3, seed: 17UL);

        Assert.Single(result.Clusters);

        Assert.Equal(["a", "b", "c"], result.Clusters[0].MemberIds);

    }

    [Fact]
    public void Cluster_NeverReturnsAnEmptyCluster()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 30).Select(index =>
                Point($"n{index:D2}", 1f, index * 0.0001f)),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 6, seed: 2UL);

        Assert.Equal(6, result.Clusters.Count);

        Assert.All(result.Clusters, static cluster => Assert.NotEmpty(cluster.MemberIds));

    }

    /// <summary>
    /// The empty-cluster repair reads each point's similarity to its own centroid out of the buffer
    /// the assignment pass already filled instead of re-deriving it. That is only sound if the
    /// buffer cannot go stale mid-repair, so this pins the exact memberships of a corpus that drives
    /// the repair hard: the fingerprint below was captured from the implementation that recomputed
    /// every similarity, and a cache that ever went stale would move a donor and change it.
    /// </summary>
    [Fact]
    public void Cluster_RepairingManyEmptyClustersMatchesRecomputedSimilarities()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 400).Select(index =>
                Point($"n{index:D4}", 1f, index * 0.00001f, 0.5f)),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 64, seed: 7UL);

        Assert.Equal(64, result.Clusters.Count);

        Assert.All(result.Clusters, static cluster => Assert.NotEmpty(cluster.MemberIds));

        string memberships = string.Join(
            "|",
            result.Clusters.Select(static cluster => string.Join(",", cluster.MemberIds)));

        Assert.Equal(
            "8db504b833897d307de7c09bac6f907c54585aeac763687b6173370c077da7b4",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(memberships))));

    }

    [Fact]
    public void Cluster_QuarantinesUnusableVectorsWithoutPoisoningTheLayer()
    {

        SphericalKMeansPoint[] points =
        [
            Point("good1", 1f, 0f),
            Point("good2", 0f, 1f),
            Point("nan", float.NaN, 1f),
            Point("infinity", float.PositiveInfinity, 0f),
            Point("zero", 0f, 0f),
            Point("short", 1f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 11UL);

        Assert.Equal(
            ["good1", "good2"],
            result.Clusters.SelectMany(static cluster => cluster.MemberIds).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["infinity", "nan", "short", "zero"],
            result.Rejected.Select(static rejected => rejected.StableId).Order(StringComparer.Ordinal));

        Assert.Equal(
            SphericalKMeansRejection.NonFiniteComponent,
            result.Rejected.Single(static rejected => rejected.StableId == "nan").Reason);

        Assert.Equal(
            SphericalKMeansRejection.ZeroNorm,
            result.Rejected.Single(static rejected => rejected.StableId == "zero").Reason);

        Assert.Equal(
            SphericalKMeansRejection.DimensionMismatch,
            result.Rejected.Single(static rejected => rejected.StableId == "short").Reason);

    }

    [Fact]
    public void Cluster_UsesTheExplicitExpectedDimensionWhenSupplied()
    {

        SphericalKMeansPoint[] points =
        [
            Point("wide1", 1f, 0f, 0f),
            Point("wide2", 0f, 1f, 0f),
            Point("narrow", 1f, 0f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(
            points,
            k: 2,
            seed: 4UL,
            expectedDimensions: 3);

        Assert.Equal(
            SphericalKMeansRejection.DimensionMismatch,
            Assert.Single(result.Rejected).Reason);

        Assert.Equal("narrow", result.Rejected[0].StableId);

    }

    [Fact]
    public void Cluster_EmptyInputProducesNoClusters()
    {

        SphericalKMeansResult result = SphericalKMeans.Cluster([], k: 4, seed: 1UL);

        Assert.Empty(result.Clusters);

        Assert.Empty(result.Rejected);

        Assert.Equal(0, result.Iterations);

        Assert.Equal(SphericalKMeansTermination.AssignmentsStable, result.Termination);

    }

    [Fact]
    public void Cluster_AllUnusableInputProducesNoClusters()
    {

        SphericalKMeansPoint[] points =
        [
            Point("zero1", 0f, 0f),
            Point("zero2", 0f, 0f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 1UL);

        Assert.Empty(result.Clusters);

        Assert.Equal(2, result.Rejected.Count);

    }

    [Fact]
    public void Cluster_StopsOnStableAssignments()
    {

        // Members are deliberately spread within each cluster so the first centroid update moves
        // further than the convergence tolerance — otherwise the run would legitimately terminate on
        // tolerance before a second assignment pass ever happens.
        SphericalKMeansPoint[] points =
        [
            Point("a1", 1f, 0f),
            Point("a2", 0.8f, 0.6f),
            Point("b1", -1f, 0f),
            Point("b2", -0.8f, -0.6f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 8UL);

        Assert.Equal(SphericalKMeansTermination.AssignmentsStable, result.Termination);

        Assert.InRange(result.Iterations, 2, SphericalKMeans.MaxIterations);

    }

    [Fact]
    public void Cluster_ConvergesWithoutReachingTheIterationCap()
    {

        SphericalKMeansPoint[] points =
        [
            Point("a1", 1f, 0f),
            Point("a2", 0.999f, 0.001f),
            Point("b1", 0f, 1f),
            Point("b2", 0.001f, 0.999f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 8UL);

        Assert.NotEqual(SphericalKMeansTermination.IterationCap, result.Termination);

        Assert.InRange(result.Iterations, 1, SphericalKMeans.MaxIterations);

        Assert.Equal(["a1", "a2"], result.Clusters[0].MemberIds);

        Assert.Equal(["b1", "b2"], result.Clusters[1].MemberIds);

    }

    [Fact]
    public void Cluster_DoesNotCollapseNearIdenticalVectorsIntoFewerClusters()
    {

        // A codebase full of near-identical boilerplate must still be partitioned into the requested
        // number of clusters: only *exactly* duplicate directions cap k.
        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 30).Select(index =>
                Point($"n{index:D2}", 1f, index * 0.0001f)),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 6, seed: 2UL);

        Assert.Equal(6, result.Clusters.Count);

    }

    [Fact]
    public void Cluster_RespectsTheIterationCap()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 12).Select(index =>
                Point($"n{index:D2}", MathF.Cos(index * 0.5f), MathF.Sin(index * 0.5f))),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(
            points,
            k: 3,
            seed: 6UL,
            maxIterations: 1);

        Assert.Equal(SphericalKMeansTermination.IterationCap, result.Termination);

        Assert.Equal(1, result.Iterations);

        Assert.Equal(12, result.Clusters.Sum(static cluster => cluster.MemberIds.Count));

    }

    [Fact]
    public void Cluster_SingleUsablePointProducesOneCluster()
    {

        SphericalKMeansResult result = SphericalKMeans.Cluster(
            [Point("only", 0.3f, 0.4f)],
            k: 3,
            seed: 1UL);

        Assert.Equal(["only"], Assert.Single(result.Clusters).MemberIds);

    }

    [Fact]
    public void Cluster_EveryUsablePointIsAssignedExactlyOnce()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 57).Select(index =>
                Point(
                    $"n{index:D3}",
                    MathF.Cos(index * 0.29f),
                    MathF.Sin(index * 0.29f),
                    MathF.Cos(index * 0.71f),
                    MathF.Sin(index * 0.13f))),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 7, seed: 21UL);

        string[] assigned = [.. result.Clusters.SelectMany(static cluster => cluster.MemberIds)];

        Assert.Equal(57, assigned.Length);

        Assert.Equal(57, assigned.Distinct(StringComparer.Ordinal).Count());

    }

    [Fact]
    public void Cluster_CentroidsAreUnitLength()
    {

        SphericalKMeansPoint[] points =
        [
            Point("a", 3f, 4f),
            Point("b", 4f, 3f),
            Point("c", -3f, -4f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 2, seed: 13UL);

        foreach (SphericalKMeansCluster cluster in result.Clusters)
        {

            double norm = Math.Sqrt(cluster.Centroid.Sum(component => (double)component * component));

            Assert.InRange(norm, 0.999, 1.001);

        }

    }

    [Fact]
    public void DeriveClusterCount_UsesCeilingOfTargetChildrenPerSummary()
    {

        Assert.Equal(
            4,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 20,
                targetChildrenPerSummary: 5,
                maxClustersPerLayer: 64,
                distinctVectorCount: 20));

        Assert.Equal(
            5,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 21,
                targetChildrenPerSummary: 5,
                maxClustersPerLayer: 64,
                distinctVectorCount: 21));

    }

    [Fact]
    public void DeriveClusterCount_ClampsToBoundsAndDistinctVectors()
    {

        Assert.Equal(
            2,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 6,
                targetChildrenPerSummary: 100,
                maxClustersPerLayer: 64,
                distinctVectorCount: 6));

        Assert.Equal(
            3,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 900,
                targetChildrenPerSummary: 2,
                maxClustersPerLayer: 3,
                distinctVectorCount: 900));

        Assert.Equal(
            2,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 900,
                targetChildrenPerSummary: 2,
                maxClustersPerLayer: 64,
                distinctVectorCount: 2));

        Assert.Equal(
            1,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 1,
                targetChildrenPerSummary: 5,
                maxClustersPerLayer: 64,
                distinctVectorCount: 1));

        Assert.Equal(
            0,
            SphericalKMeans.DeriveClusterCount(
                nodeCount: 0,
                targetChildrenPerSummary: 5,
                maxClustersPerLayer: 64,
                distinctVectorCount: 0));

    }

    [Fact]
    public void Cluster_ObservesCancellationDuringSeeding()
    {

        // Enough clusters that seeding runs several rounds: without a token reaching the algorithm,
        // clustering a real layer is minutes of uninterruptible CPU inside one synchronous call.
        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 64).Select(index => Point(
                $"p{index:D3}",
                MathF.Cos(index * 0.1f),
                MathF.Sin(index * 0.1f))),
        ];

        using CancellationTokenSource cts = new();

        cts.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() =>
            SphericalKMeans.Cluster(points, k: 16, seed: 99UL, cancellationToken: cts.Token));

    }

    [Fact]
    public void ClusterLayer_ObservesCancellation()
    {

        SphericalKMeansPoint[] points =
        [
            .. Enumerable.Range(0, 64).Select(index => Point(
                $"p{index:D3}",
                MathF.Cos(index * 0.1f),
                MathF.Sin(index * 0.1f))),
        ];

        using CancellationTokenSource cts = new();

        cts.Cancel();

        _ = Assert.Throws<OperationCanceledException>(() =>
            SphericalKMeans.ClusterLayer(
                points,
                targetChildrenPerSummary: 4,
                maxClustersPerLayer: 16,
                seed: 99UL,
                cancellationToken: cts.Token));

    }

    [Fact]
    public void Cluster_SeedingIsIncrementalYetProducesTheDocumentedMemberships()
    {

        // Pins the K-Means++ selection weights that the incremental nearest-distance array must
        // reproduce exactly. A running minimum is exact in floating point, so collapsing the per-round
        // rescan of every chosen centroid into one fold of the newest centroid cannot move a boundary.
        SphericalKMeansPoint[] points =
        [
            Point("a1", 1f, 0f, 0f),
            Point("a2", 0.98f, 0.02f, 0f),
            Point("b1", 0f, 1f, 0f),
            Point("b2", 0.02f, 0.98f, 0f),
            Point("c1", 0f, 0f, 1f),
            Point("c2", 0f, 0.02f, 0.98f),
        ];

        SphericalKMeansResult result = SphericalKMeans.Cluster(points, k: 3, seed: 4242UL);

        Assert.Equal(3, result.Clusters.Count);

        Assert.Empty(result.Rejected);

        string[][] memberships = [.. result.Clusters.Select(static cluster => cluster.MemberIds.ToArray())];

        Assert.Contains(memberships, static members => members.SequenceEqual(new[] { "a1", "a2" }));

        Assert.Contains(memberships, static members => members.SequenceEqual(new[] { "b1", "b2" }));

        Assert.Contains(memberships, static members => members.SequenceEqual(new[] { "c1", "c2" }));

    }

}
