using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

public sealed class GrimoireAdmissionBenchmarkManifestTests
{

    private static readonly string[] ExactOperations =
    [
        "request.finite",
        "request.quiesceable",
        "work.db-only",
        "work.effect",
        "open.failed",
        "request.open",
        "generation.read",
        "ordinary.mixed",
        "ef.pooled",
    ];

    [Fact]
    public void Checked_in_manifest_is_closed_positive_and_source_generated_round_trips()
    {

        string path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
            "grimoire-admission-workload-v1.json");

        AdmissionBenchmarkManifest manifest = AdmissionBenchmarkManifest.Parse(
            File.ReadAllText(path),
            ManifestTestJsonContext.Default.AdmissionBenchmarkManifest);

        Assert.Equal(1, manifest.SchemaVersion);

        Assert.Equal(ExactOperations, manifest.Operations);

        Assert.Equal(
            ["one:1", "two:2", "eight:8", "logical:0"],
            manifest.Concurrency.Select(static value => $"{value.Id}:{value.Workers}"));

        Assert.Equal(["qualification", "smoke"], manifest.Profiles.Select(static profile => profile.Name));

        Assert.All(
            manifest.Profiles,
            static profile =>
            {

                Assert.True(profile.WarmupIterations > 0);

                Assert.True(profile.LatencyBundleSize > 0);

                Assert.True(profile.LatencySampleCount > 0);

                Assert.True(profile.ThroughputIterations > 0);

                Assert.NotEmpty(profile.MixedSchedule);

            });

        Assert.Equal("ordinary.mixed", manifest.MaterialImprovementOperation);

        Assert.Equal("logical", manifest.MaterialImprovementConcurrency);

        Assert.Equal(1.20, manifest.Thresholds.MixedThroughputMinimumRatio);

        Assert.Equal(1.05, manifest.Thresholds.SingleThreadP50MaximumRatio);

        Assert.Equal(1.10, manifest.Thresholds.P99MaximumRatio);

        Assert.Equal(1.05, manifest.Thresholds.EfMaximumRatio);

        Assert.Equal(1.05, manifest.Thresholds.EfAllocationMaximumRatio);

        Assert.Equal(
            [
                "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs",
            ],
            manifest.SourceDifferenceAllowlist);

        string json = JsonSerializer.Serialize(
            manifest,
            ManifestTestJsonContext.Default.AdmissionBenchmarkManifest);

        AdmissionBenchmarkManifest roundTripped = JsonSerializer.Deserialize(
            json,
            ManifestTestJsonContext.Default.AdmissionBenchmarkManifest)!;

        Assert.Equal(manifest.SchemaVersion, roundTripped.SchemaVersion);

        Assert.Equal(manifest.Operations, roundTripped.Operations);

        Assert.Equal(manifest.Concurrency, roundTripped.Concurrency);

        Assert.Equal(manifest.Profiles.Length, roundTripped.Profiles.Length);

        for (int index = 0; index < manifest.Profiles.Length; index++)
        {

            Assert.Equal(manifest.Profiles[index].Name, roundTripped.Profiles[index].Name);

            Assert.Equal(manifest.Profiles[index].WarmupIterations, roundTripped.Profiles[index].WarmupIterations);

            Assert.Equal(manifest.Profiles[index].LatencyBundleSize, roundTripped.Profiles[index].LatencyBundleSize);

            Assert.Equal(manifest.Profiles[index].LatencySampleCount, roundTripped.Profiles[index].LatencySampleCount);

            Assert.Equal(manifest.Profiles[index].ThroughputIterations, roundTripped.Profiles[index].ThroughputIterations);

            Assert.Equal(manifest.Profiles[index].MixedSchedule, roundTripped.Profiles[index].MixedSchedule);

        }

        Assert.Equal(manifest.Thresholds, roundTripped.Thresholds);

        Assert.Equal(manifest.SourceDifferenceAllowlist, roundTripped.SourceDifferenceAllowlist);

    }

    [Theory]
    [InlineData("unknown-operation")]
    [InlineData("duplicate-operation")]
    [InlineData("missing-operation")]
    [InlineData("duplicate-concurrency")]
    [InlineData("negative-threshold")]
    [InlineData("non-finite-threshold")]
    [InlineData("wildcard-allowlist")]
    [InlineData("directory-allowlist")]
    public void Parser_rejects_open_or_malformed_manifest_shapes(string mutation)
    {

        AdmissionBenchmarkManifest valid = AdmissionBenchmarkManifest.CreateDefault();

        string json = mutation == "non-finite-threshold"
            ? JsonSerializer.Serialize(
                valid,
                ManifestTestJsonContext.Default.AdmissionBenchmarkManifest)
                .Replace("\"p99MaximumRatio\":1.1", "\"p99MaximumRatio\":1e999", StringComparison.Ordinal)
            : JsonSerializer.Serialize(
                Mutate(valid, mutation),
                ManifestTestJsonContext.Default.AdmissionBenchmarkManifest);

        Assert.Throws<InvalidDataException>(
            () => AdmissionBenchmarkManifest.Parse(
                json,
                ManifestTestJsonContext.Default.AdmissionBenchmarkManifest));

    }

    private static AdmissionBenchmarkManifest Mutate(
        AdmissionBenchmarkManifest manifest,
        string mutation) =>
        mutation switch
        {
            "unknown-operation" => manifest with { Operations = [.. manifest.Operations, "unknown"] },
            "duplicate-operation" => manifest with { Operations = [.. manifest.Operations, manifest.Operations[0]] },
            "missing-operation" => manifest with { Operations = manifest.Operations[1..] },
            "duplicate-concurrency" => manifest with { Concurrency = [.. manifest.Concurrency, manifest.Concurrency[0]] },
            "negative-threshold" => manifest with { Thresholds = manifest.Thresholds with { P99MaximumRatio = -1 } },
            "wildcard-allowlist" => manifest with { SourceDifferenceAllowlist = ["src/**/*.cs"] },
            "directory-allowlist" => manifest with { SourceDifferenceAllowlist = ["src/RetroDownfall.Arcanum.Infrastructure/Data"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AdmissionBenchmarkManifest))]
internal sealed partial class ManifestTestJsonContext : JsonSerializerContext
{
}
