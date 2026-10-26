using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor;

internal static class RaptorReferenceFixtures
{
    internal static string Root => TestRepositoryPaths.RepositoryRoot();

    internal static string Directory => Path.Combine(Root, "tests", "RetroDownfall.Arcanum.Tests", "Fixtures", "Raptor");

    internal static RaptorReferenceManifest ReadManifest() => JsonSerializer.Deserialize(
        File.ReadAllBytes(Path.Combine(Directory, "manifest.json")), RaptorFixtureJsonContext.Default.RaptorReferenceManifest)!;

    internal static RaptorGaussianFixture Gaussian() => Read("gaussian.json", RaptorFixtureJsonContext.Default.RaptorGaussianFixture);

    internal static RaptorFuzzyFixture Fuzzy() => Read("fuzzy-graph.json", RaptorFixtureJsonContext.Default.RaptorFuzzyFixture);

    internal static RaptorOptimizerFixture Optimizer() => Read("optimizer-step.json", RaptorFixtureJsonContext.Default.RaptorOptimizerFixture);

    private static T Read<T>(string name, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        RaptorReferenceManifest manifest = ReadManifest();

        manifest.Validate();

        byte[] bytes = File.ReadAllBytes(Path.Combine(Directory, name));

        manifest.VerifyFixture(name, bytes);

        return JsonSerializer.Deserialize(bytes, typeInfo)!;
    }
}

internal sealed record RaptorReferenceArtifact(string Name, string Version, string Filename, string Url, string Sha256);

internal sealed record RaptorReferenceSource(string Name, string Url, string Sha256);

internal sealed record RaptorKernelSource(string Name, string Sha256);

internal sealed record RaptorReferenceManifest(
    string Recipe,
    string RaptorCommit,
    string PythonVersion,
    string Platform,
    int Seed,
    RaptorReferenceSource ReferenceSource,
    string LockfileSha256,
    string GeneratorSha256,
    RaptorReferenceArtifact[] Artifacts,
    RaptorKernelSource[] Kernels,
    Dictionary<string, string> Fixtures,
    string[] Limitations)
{
    internal void Validate()
    {
        const string commit = "7da1d48a7e1d7dec61a63c9d9aae84e2dfaa5767";

        Require(Recipe == "raptor-reference-v1" && RaptorCommit == commit && PythonVersion == "3.11.16" && Seed == 224, "Unsupported reference recipe or source identity.");

        Require(!string.IsNullOrWhiteSpace(Platform), "Missing reference platform.");

        Require(ReferenceSource is not null && ReferenceSource.Name == "raptor/cluster_utils.py"
            && ReferenceSource.Url == $"https://raw.githubusercontent.com/parthsarthi03/raptor/{commit}/raptor/cluster_utils.py"
            && ReferenceSource.Sha256 == "bfe3fa5c5ddf6741b3d1fa1747203d6899c1520aa6d3d167cbab34445de78317", "Missing or corrupt official RAPTOR source identity.");

        Require(IsHash(LockfileSha256) && IsHash(GeneratorSha256), "Missing generator or lock hash.");

        string referenceDirectory = Path.Combine(RaptorReferenceFixtures.Root, "scripts", "raptor-reference");

        string lockfile = File.ReadAllText(Path.Combine(referenceDirectory, "requirements.lock"));

        Require(Hash(File.ReadAllBytes(Path.Combine(referenceDirectory, "requirements.lock"))) == LockfileSha256, "Reference lock drifted.");

        Require(Hash(File.ReadAllBytes(Path.Combine(referenceDirectory, "generate.py"))) == GeneratorSha256, "Reference generator drifted.");

        Dictionary<string, string> expectedPackages = new(StringComparer.Ordinal)
        {
            ["joblib"] = "1.4.2",
            ["llvmlite"] = "0.42.0",
            ["numba"] = "0.59.1",
            ["numpy"] = "1.26.3",
            ["pynndescent"] = "0.5.12",
            ["scikit-learn"] = "1.4.2",
            ["scipy"] = "1.12.0",
            ["setuptools"] = "75.8.0",
            ["threadpoolctl"] = "3.5.0",
            ["tqdm"] = "4.66.5",
            ["umap-learn"] = "0.5.5",
            ["wheel"] = "0.45.1",
        };

        Require(Artifacts is not null && Artifacts.Length == expectedPackages.Count, "Incomplete reference dependency closure.");

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (RaptorReferenceArtifact artifact in Artifacts!)
        {
            Require(artifact is not null && artifact.Name is not null && expectedPackages.TryGetValue(artifact.Name, out string? version)
                && artifact.Version == version && seen.Add(artifact.Name), "Unsupported or repeated reference dependency.");

            Require(IsHash(artifact!.Sha256) && !string.IsNullOrWhiteSpace(artifact.Filename)
                && !artifact.Filename.Contains('/') && !artifact.Filename.Contains('\\')
                && Uri.TryCreate(artifact.Url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" && uri.Host == "files.pythonhosted.org", "Missing reference artifact provenance.");

            Match entry = Regex.Match(lockfile, $@"(?m)^{Regex.Escape(artifact.Name)}=={Regex.Escape(artifact.Version)}(?: \\)?\r?\n(?:[ \t]+--hash=sha256:[a-f0-9]{{64}}(?: \\)?\r?\n)+");

            Require(entry.Success && entry.Value.Contains($"--hash=sha256:{artifact.Sha256}", StringComparison.Ordinal), "Reference artifact hash is outside the lock.");
        }

        Dictionary<string, string> expectedKernels = new(StringComparer.Ordinal)
        {
            ["sklearn/mixture/_gaussian_mixture.py"] = "5e7e0e6afab43d939324d5f56f0df620c4933668ff8dec313714cb89857a8d0b",
            ["umap/layouts.py"] = "d8ac84926587b9680135ba6ea5f85e0d1d5c67e3712efe78aacf3a142837c826",
            ["umap/umap_.py"] = "217f4dab6b061cff419afb1999b321de7f5628760909484a39ea0cb9e402d37f",
        };

        Require(Kernels is not null && Kernels.Length == expectedKernels.Count, "Incomplete reference kernel sources.");

        seen.Clear();

        foreach (RaptorKernelSource kernel in Kernels!)
        {
            Require(kernel is not null && kernel.Name is not null && seen.Add(kernel.Name)
                && expectedKernels.TryGetValue(kernel.Name, out string? hash) && kernel.Sha256 == hash, "Reference kernel source drifted.");
        }

        Require(Fixtures is not null && Fixtures.Count == 3
            && new[] { "gaussian.json", "fuzzy-graph.json", "optimizer-step.json" }.All(Fixtures.ContainsKey)
            && Fixtures.Values.All(IsHash), "Incomplete numeric fixture hashes.");
    }

    internal void VerifyFixture(string name, byte[] bytes)
    {
        Require(Fixtures is not null && Fixtures.TryGetValue(name, out string? expected) && IsHash(expected)
            && Hash(bytes) == expected, "Missing or corrupt numeric fixture.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Require([DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}

internal sealed record RaptorGaussianFixture(
    string Recipe,
    int Seed,
    string Dtype,
    double[][] Points,
    double[][] Means,
    double[][][] Covariances,
    double[] Weights,
    double[][] LogPdf,
    double[][][] Cholesky,
    double[] LogDeterminants,
    double[][] Responsibilities,
    double Threshold,
    int[][] Memberships,
    RaptorGaussianAnchor Analytic,
    double[][] EmPoints,
    RaptorGaussianFit[] Fits);

internal sealed record RaptorGaussianAnchor(double[] Point, double[] Mean, double[][] Covariance, double[][] Cholesky, double LogDeterminant, double LogPdf);

internal sealed record RaptorGaussianFit(
    int Components,
    double[] InitialWeights,
    double[][] InitialMeans,
    double[][][] InitialCovariances,
    bool Converged,
    int Iterations,
    double[] Weights,
    double[][] Means,
    double[][][] Covariances,
    double[][] Responsibilities,
    double TotalLogLikelihood,
    double Bic);

internal sealed record RaptorFuzzyFixture(string Recipe, int Seed, string Dtype, string SelfNeighbor, double Tolerance, RaptorFuzzyCase[] Cases);

internal sealed record RaptorFuzzyCase(
    string Name,
    double[][] Points,
    int Neighbors,
    int[][] Indices,
    double[][] Distances,
    double[] Sigmas,
    double[] Rhos,
    double[][] Directed,
    double[][] Symmetric);

internal sealed record RaptorOptimizerFixture(
    string Recipe,
    int Seed,
    string Dtype,
    double Tolerance,
    double MinDist,
    double Spread,
    double FittedA,
    double FittedB,
    int NegativeSampleRate,
    double GradientClip,
    double RepulsionDistanceFloor,
    RaptorOptimizerCase[] Cases);

internal sealed record RaptorOptimizerCase(
    string Name,
    double[][] Initial,
    int[] Head,
    int[] Tail,
    int Epoch,
    double[] EpochsPerSample,
    double[] InitialNextSample,
    double[] EpochsPerNegativeSample,
    double[] InitialNextNegative,
    double[] NextSample,
    double[] NextNegative,
    long[] InitialRng,
    long[] FinalRng,
    int[][] NegativeSamples,
    double A,
    double B,
    double Alpha,
    double Gamma,
    bool MoveOther,
    double[][] Expected,
    double[][] Delta);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RaptorReferenceManifest))]
[JsonSerializable(typeof(RaptorGaussianFixture))]
[JsonSerializable(typeof(RaptorFuzzyFixture))]
[JsonSerializable(typeof(RaptorOptimizerFixture))]
internal sealed partial class RaptorFixtureJsonContext : JsonSerializerContext;
