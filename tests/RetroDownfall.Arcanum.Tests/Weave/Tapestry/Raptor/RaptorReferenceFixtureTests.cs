using System.Text;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

public sealed class RaptorReferenceFixtureTests
{
    [Fact]
    public void ReferenceManifest_RejectsMissingCommitOrArtifactHash()
    {
        RaptorReferenceManifest baseline = RaptorReferenceFixtures.ReadManifest();

        Assert.Throws<InvalidDataException>(() => (baseline with { RaptorCommit = "" }).Validate());

        RaptorReferenceArtifact[] missingHash = [.. baseline.Artifacts];

        missingHash[0] = missingHash[0] with { Sha256 = "" };

        Assert.Throws<InvalidDataException>(() => (baseline with { Artifacts = missingHash }).Validate());
    }

    [Fact]
    public void ReferenceManifest_RejectsUnsupportedRecipeAndIncompleteDependencyClosure()
    {
        RaptorReferenceManifest baseline = RaptorReferenceFixtures.ReadManifest();

        Assert.Throws<InvalidDataException>(() => (baseline with { Recipe = "raptor-reference-v2" }).Validate());

        Assert.Throws<InvalidDataException>(() => (baseline with { Artifacts = baseline.Artifacts[1..] }).Validate());

        RaptorReferenceArtifact[] wrongVersion = [.. baseline.Artifacts];

        wrongVersion[0] = wrongVersion[0] with { Version = "999.0" };

        Assert.Throws<InvalidDataException>(() => (baseline with { Artifacts = wrongVersion }).Validate());

        Assert.Throws<InvalidDataException>(() => (baseline with { Seed = 225 }).Validate());
    }

    [Fact]
    public void ReferenceManifest_RejectsCorruptFixtureAndMissingGeneratorHash()
    {
        RaptorReferenceManifest manifest = RaptorReferenceFixtures.ReadManifest();

        Assert.Throws<InvalidDataException>(() => manifest.VerifyFixture("gaussian.json", Encoding.UTF8.GetBytes("{}")));

        Assert.Throws<InvalidDataException>(() => manifest.VerifyFixture("unknown.json", []));

        Assert.Throws<InvalidDataException>(() => (manifest with { GeneratorSha256 = "" }).Validate());
    }

    [Fact]
    public void CheckedInFixtures_HaveVerifiedProvenanceAndFiniteBoundedArrays()
    {
        RaptorReferenceManifest manifest = RaptorReferenceFixtures.ReadManifest();

        manifest.Validate();

        foreach (string name in manifest.Fixtures.Keys)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(RaptorReferenceFixtures.Directory, name));

            manifest.VerifyFixture(name, bytes);

            Assert.InRange(bytes.Length, 1, 100_000);

            using JsonDocument document = JsonDocument.Parse(bytes);

            CheckBounded(document.RootElement);
        }

        Assert.NotNull(RaptorReferenceFixtures.Gaussian());

        Assert.NotNull(RaptorReferenceFixtures.Fuzzy());

        Assert.NotNull(RaptorReferenceFixtures.Optimizer());
    }

    [Fact]
    public void GaussianReference_PreservesAnalyticAnchorAndNormalizedOverlap()
    {
        RaptorGaussianFixture fixture = RaptorReferenceFixtures.Gaussian();

        Assert.Equal([2d, 0d], fixture.Analytic.Cholesky[0]);

        Assert.Equal([0d, 3d], fixture.Analytic.Cholesky[1]);

        Assert.Equal(Math.Log(36), fixture.Analytic.LogDeterminant, 12);

        Assert.Equal(-Math.Log(12 * Math.PI), fixture.Analytic.LogPdf, 12);

        Assert.Contains(fixture.Memberships, members => members.Length > 1);

        foreach (double[] row in fixture.Responsibilities)
        {
            Assert.Equal(1, row.Sum(), 12);

            Assert.All(row, probability => Assert.InRange(probability, 0, 1));
        }

        foreach (RaptorGaussianFit fit in fixture.Fits)
        {
            Assert.True(fit.Converged);

            Assert.InRange(fit.Iterations, 1, 100);

            int k = fit.Components;

            int dimension = fixture.EmPoints[0].Length;

            int parameterCount = k - 1 + k * dimension + k * dimension * (dimension + 1) / 2;

            Assert.Equal(-2 * fit.TotalLogLikelihood + parameterCount * Math.Log(fixture.EmPoints.Length), fit.Bic, 10);
        }
    }

    [Fact]
    public void FuzzyReference_PreservesDuplicateIndicesAndRealWeightedUnion()
    {
        RaptorFuzzyCase fixture = RaptorReferenceFixtures.Fuzzy().Cases.Single(item => item.Name == "ties-and-duplicates");

        Assert.Equal(0, fixture.Indices[0][0]);

        Assert.Equal(1, fixture.Indices[1][0]);

        Assert.Equal(0, fixture.Directed[0][0]);

        Assert.Equal(1, fixture.Directed[0][1]);

        Assert.Contains(fixture.Directed.SelectMany(row => row), weight => weight > 0 && weight < 1);

        for (int row = 0; row < fixture.Points.Length; row++)
        {
            for (int column = 0; column < fixture.Points.Length; column++)
            {
                double a = fixture.Directed[row][column];

                double b = fixture.Directed[column][row];

                Assert.InRange(Math.Abs(a + b - a * b - fixture.Symmetric[row][column]), 0, 1e-6);
            }
        }
    }

    [Fact]
    public void OptimizerReference_PreservesClosedFormAttractionAndExplicitNegativeSampleTape()
    {
        RaptorOptimizerFixture fixture = RaptorReferenceFixtures.Optimizer();

        RaptorOptimizerCase analytic = fixture.Cases.Single(item => item.Name == "analytic-attraction");

        Assert.Equal([0.25, 0d], analytic.Expected[0]);

        Assert.Equal([0.75, 0d], analytic.Expected[1]);

        Assert.Empty(analytic.NegativeSamples[0]);

        RaptorOptimizerCase sampled = fixture.Cases.Single(item => item.Name == "attraction-and-negative-sampling");

        Assert.All(sampled.NegativeSamples, samples => Assert.Equal(4, samples.Length));

        Assert.NotEqual(sampled.InitialRng, sampled.FinalRng);

        Assert.NotEqual(sampled.Initial[0], sampled.Expected[0]);
    }

    private static void CheckBounded(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                Assert.InRange(element.GetArrayLength(), 0, 256);

                foreach (JsonElement child in element.EnumerateArray())
                {
                    CheckBounded(child);
                }

                break;

            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    CheckBounded(property.Value);
                }

                break;

            case JsonValueKind.Number:
                Assert.True(double.IsFinite(element.GetDouble()));

                break;
        }
    }
}
