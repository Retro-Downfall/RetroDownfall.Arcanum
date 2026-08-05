using RetroDownfall.Arcanum.Core.Weave.Tapestry;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry;

/// <summary>
/// Seeds are derived from the versioned clustering-algorithm id plus tree scope and layer — never
/// process-random state — so a rebuild of the same scope reproduces the same memberships
/// (DESIGN §21.11).
/// </summary>
public sealed class TapestrySeedTests
{

    [Fact]
    public void Derive_IsStableForTheSameScopeAndLayer()
    {

        ulong first = TapestrySeed.Derive(
            SphericalKMeans.AlgorithmVersion,
            TapestryScopeKind.Workspace,
            "/repo/one",
            layer: 1);

        ulong second = TapestrySeed.Derive(
            SphericalKMeans.AlgorithmVersion,
            TapestryScopeKind.Workspace,
            "/repo/one",
            layer: 1);

        Assert.Equal(first, second);

    }

    [Fact]
    public void Derive_DiffersByLayerScopeAndAlgorithmVersion()
    {

        ulong baseline = TapestrySeed.Derive(
            SphericalKMeans.AlgorithmVersion,
            TapestryScopeKind.Workspace,
            "/repo/one",
            layer: 1);

        Assert.NotEqual(
            baseline,
            TapestrySeed.Derive(
                SphericalKMeans.AlgorithmVersion,
                TapestryScopeKind.Workspace,
                "/repo/one",
                layer: 2));

        Assert.NotEqual(
            baseline,
            TapestrySeed.Derive(
                SphericalKMeans.AlgorithmVersion,
                TapestryScopeKind.Workspace,
                "/repo/two",
                layer: 1));

        Assert.NotEqual(
            baseline,
            TapestrySeed.Derive(
                SphericalKMeans.AlgorithmVersion,
                TapestryScopeKind.Session,
                "/repo/one",
                layer: 1));

        Assert.NotEqual(
            baseline,
            TapestrySeed.Derive(
                "spherical-kmeans-v2",
                TapestryScopeKind.Workspace,
                "/repo/one",
                layer: 1));

    }

    [Fact]
    public void DeterministicRandom_ReproducesTheSameSequenceForTheSameSeed()
    {

        TapestryDeterministicRandom first = new(4242UL);

        TapestryDeterministicRandom second = new(4242UL);

        for (int index = 0; index < 32; index++)
        {

            Assert.Equal(first.NextDouble(), second.NextDouble());

        }

    }

    [Fact]
    public void DeterministicRandom_StaysInTheUnitInterval()
    {

        TapestryDeterministicRandom random = new(1UL);

        for (int index = 0; index < 1_000; index++)
        {

            double value = random.NextDouble();

            Assert.InRange(value, 0d, 1d);

            Assert.NotEqual(1d, value);

        }

    }

}
