using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The Tapestry's clustering contract (DESIGN §21.11) is stated over scalar arithmetic, and every
/// similarity the weaver compares has to be computed that way.
/// </summary>
/// <remarks>
/// <see cref="RetroDownfall.Arcanum.Core.Primitives.EmbeddingBlobCodec"/>'s cosine sums its products in
/// hardware-width lanes, so its low bits depend on the vector width the machine reports. That is
/// right for Divination, which only ranks, and wrong for the weaver's undersized-cluster merge, whose
/// target choice must be the same on every machine for the same persisted vectors to produce the same
/// memberships. The merge cannot be driven to a different answer by a few ulps in a fixture that stays
/// legible, so the contract is pinned where it can be seen: the weaver names no lane-width cosine.
/// </remarks>
public sealed class TapestryClusteringContractTests
{
    private static string WeaverSource() =>
        File.ReadAllText(
            Path.Combine(
                TestRepositoryPaths.RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Weave",
                "TapestryWeaver.cs"));

    [Fact]
    public void The_weaver_merges_undersized_clusters_with_the_scalar_cosine_the_clustering_contract_assumes()
    {
        string source = WeaverSource();

        Assert.DoesNotContain("EmbeddingBlobCodec.CosineSimilarity", source, StringComparison.Ordinal);

        Assert.DoesNotContain("TryCosineSimilarity", source, StringComparison.Ordinal);

        Assert.Contains("SphericalKMeans.DirectionCosine", source, StringComparison.Ordinal);
    }
}
