using System.Buffers.Binary;

using System.Diagnostics;

using System.Security.Cryptography;

using System.Text;

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
    public void Cell_contract_records_exact_phase_and_raw_allocation_accounting()
    {
        string[] requiredProperties =
        [
            "WarmupOperationCount",
            "LatencyBundleCount",
            "LatencyOperationCount",
            "ThroughputOperationCount",
            "AllocatedBytes",
            "AllocationOperationCount",
        ];

        string[] actual = typeof(AdmissionBenchmarkCellResult)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        Assert.All(requiredProperties, property => Assert.Contains(property, actual));
    }

    [Fact]
    public void Exact_count_and_mixed_checksum_match_a_hand_derived_partition_fixture()
    {
        AdmissionBenchmarkProfile profile = new(
            "literal",
            3,
            2,
            2,
            5,
            10,
            ["request.finite", "request.open"]);

        Assert.Equal(12, AdmissionBenchmarkExpected.OperationCount(profile));

        Assert.Equal(18, AdmissionBenchmarkExpected.Checksum(profile, "ordinary.mixed", workers: 2));
    }

    [Fact]
    public void Checked_in_input_catalog_exactly_closes_every_tracked_benchmark_and_product_input()
    {
        string root = FindRepositoryRoot();

        const string catalogPath = "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt";

        string fullCatalogPath = Path.Combine(root, catalogPath);

        Assert.True(File.Exists(fullCatalogPath), "The checked input catalog is missing.");

        (string Path, bool Optional)[] catalog = File.ReadAllLines(fullCatalogPath)
            .Select(
                static line =>
                {
                    string[] parts = line.Split('\t');

                    Assert.Equal(2, parts.Length);

                    Assert.Contains(parts[0], new[] { "R", "O" });

                    return (parts[1], parts[0] == "O");
                })
            .ToArray();

        Assert.Equal(
            catalog.OrderBy(static entry => entry.Path, StringComparer.Ordinal),
            catalog);

        Assert.Equal(catalog.Length, catalog.Select(static entry => entry.Path).Distinct(StringComparer.Ordinal).Count());

        Assert.All(
            catalog,
            static entry =>
            {
                Assert.False(Path.IsPathFullyQualified(entry.Path));

                Assert.DoesNotContain('\\', entry.Path);

                Assert.DoesNotContain(
                    entry.Path.Split('/'),
                    static segment => segment is "" or "." or "..");
            });

        string[] tracked = GitTrackedFiles(root);

        HashSet<string> expected = tracked.Where(
                static path => IsSelectedCSharp(path)
                    || path.StartsWith(
                        "src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/",
                        StringComparison.Ordinal)
                        && path.EndsWith(".sql", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        expected.UnionWith(
        [
            catalogPath,
            "Directory.Build.props",
            "Directory.Build.targets",
            "scripts/benchmark-grimoire-admission.sh",
            "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json",
            "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/packages.lock.json",
            "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj",
            "src/RetroDownfall.Arcanum.Core/RetroDownfall.Arcanum.Core.csproj",
            "src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj",
            "src/RetroDownfall.Arcanum.Secrets/RetroDownfall.Arcanum.Secrets.csproj",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/RetroDownfall.Arcanum.NativeSqlCipher.csproj",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/build/RetroDownfall.Arcanum.NativeSqlCipher.targets",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/buildTransitive/RetroDownfall.Arcanum.NativeSqlCipher.targets",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/osx-arm64/native/libe_sqlcipher.dylib",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-arm64/native/e_sqlcipher.dll",
            "src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-x64/native/e_sqlcipher.dll",
            "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs",
        ]);

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            catalog.Select(static entry => entry.Path));

        (string Path, bool Optional) optional = Assert.Single(catalog, static entry => entry.Optional);

        Assert.EndsWith("GrimoireConnectionAdmissionEpoch.cs", optional.Path, StringComparison.Ordinal);

        Assert.Equal(415, catalog.Count(static entry => entry.Path.EndsWith(".sql", StringComparison.Ordinal)));

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json")));

        Assert.Equal(
            ShapeDigest(catalog),
            manifest.RootElement.GetProperty("inputCatalogShapeDigest").GetString());
    }

    [Fact]
    public void Checked_in_manifest_is_closed_positive_and_source_generated_round_trips()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks",
            "grimoire-admission-workload-v1.json");

        using JsonDocument checkedInJson = JsonDocument.Parse(File.ReadAllText(path));

        int[] deadlines = checkedInJson.RootElement.GetProperty("profiles")
            .EnumerateArray()
            .Select(static profile => profile.GetProperty("maximumDurationSeconds").GetInt32())
            .ToArray();

        Assert.Equal([900, 120], deadlines);

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

                Assert.True(profile.MaximumDurationSeconds > 0);

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

            Assert.Equal(manifest.Profiles[index].MaximumDurationSeconds, roundTripped.Profiles[index].MaximumDurationSeconds);

            Assert.Equal(manifest.Profiles[index].MixedSchedule, roundTripped.Profiles[index].MixedSchedule);
        }

        Assert.Equal(manifest.Thresholds, roundTripped.Thresholds);

        Assert.Equal(manifest.InputCatalogShapeDigest, roundTripped.InputCatalogShapeDigest);

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

    private static bool IsSelectedCSharp(string path) =>
        path.EndsWith(".cs", StringComparison.Ordinal)
        && (path.StartsWith(
                "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/",
                StringComparison.Ordinal)
            || path.StartsWith("src/RetroDownfall.Arcanum.Core/", StringComparison.Ordinal)
            || path.StartsWith("src/RetroDownfall.Arcanum.Infrastructure/", StringComparison.Ordinal)
            || path.StartsWith("src/RetroDownfall.Arcanum.Secrets/", StringComparison.Ordinal));

    private static string[] GitTrackedFiles(string root)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        start.ArgumentList.Add("ls-files");

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.Start(start)!;

        string output = process.StandardOutput.ReadToEnd();

        string error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ShapeDigest(IEnumerable<(string Path, bool Optional)> entries)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> length = stackalloc byte[sizeof(int)];

        foreach ((string path, bool optional) in entries)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);

            BinaryPrimitives.WriteInt32LittleEndian(length, pathBytes.Length);

            hash.AppendData(length);

            hash.AppendData(pathBytes);

            hash.AppendData([optional ? (byte)1 : (byte)0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FindRepositoryRoot() =>
        global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AdmissionBenchmarkManifest))]
internal sealed partial class ManifestTestJsonContext : JsonSerializerContext
{
}
