using System.Text.Json;

using System.Text.Json.Nodes;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

using Xunit;

namespace RetroDownfall.Arcanum.Tests.Benchmarks;

public sealed class GrimoireAdmissionBenchmarkEvidenceTests
{
    [Fact]
    public void Exact_clean_ancestor_bound_evidence_accepts_epoch_added_only_at_candidate()
    {
        AdmissionBenchmarkEvidenceValidation validation = AdmissionBenchmarkEvidence.Validate(
            AdmissionBenchmarkManifest.CreateDefault(),
            Bundle(),
            baseIsAncestor: true);

        Assert.True(validation.Valid, string.Join(global::System.Environment.NewLine, validation.Errors));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("non-commit")]
    [InlineData("reversed-ancestry")]
    [InlineData("session")]
    [InlineData("harness")]
    [InlineData("manifest")]
    [InlineData("project")]
    [InlineData("lock")]
    [InlineData("toolchain")]
    [InlineData("native")]
    [InlineData("environment")]
    [InlineData("outside-source")]
    [InlineData("duplicate-source")]
    [InlineData("gate-absent")]
    [InlineData("crash")]
    [InlineData("cancel")]
    [InlineData("leak")]
    [InlineData("historical-churn")]
    [InlineData("candidate-source")]
    [InlineData("catalog-shape")]
    [InlineData("immutable-content")]
    [InlineData("malformed-digest")]
    [InlineData("unsorted-input")]
    [InlineData("absolute-input")]
    [InlineData("missing-input")]
    [InlineData("extra-input")]
    [InlineData("null-input-map")]
    public void Identity_or_execution_drift_is_invalid_evidence(string breakName)
    {
        AdmissionBenchmarkEvidenceBundle evidence = Mutate(Bundle(), breakName);

        AdmissionBenchmarkEvidenceValidation validation = AdmissionBenchmarkEvidence.Validate(
            AdmissionBenchmarkManifest.CreateDefault(),
            evidence,
            baseIsAncestor: breakName != "reversed-ancestry");

        Assert.False(validation.Valid);

        string expectedErrorPrefix = breakName switch
        {
            "missing-input" or "extra-input" => "input-digest",
            "malformed-digest" => "source-map",
            "unsorted-input" or "null-input-map" => "duplicate-source",
            "absolute-input" => "source-map",
            _ => breakName,
        };

        Assert.Contains(validation.Errors, error => error.StartsWith(expectedErrorPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Truncated_bundle_json_is_refused()
    {
        string json = JsonSerializer.Serialize(
            Bundle(),
            EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle);

        Assert.Throws<InvalidDataException>(
            () => AdmissionBenchmarkEvidence.ParseBundle(
                json[..^5],
                EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle));
    }

    [Theory]
    [InlineData("p50Nanoseconds")]
    [InlineData("p95Nanoseconds")]
    [InlineData("p99Nanoseconds")]
    [InlineData("allocatedBytes")]
    [InlineData("allocationOperationCount")]
    [InlineData("bytesPerOperation")]
    [InlineData("gen0Collections")]
    [InlineData("lockContentions")]
    [InlineData("materializedTerminalCallbackDelta")]
    public void Missing_required_cell_metric_is_refused_during_json_parsing(string propertyName)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(
            Bundle(),
            EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle))!.AsObject();

        JsonObject cell = root["pairs"]![0]!["candidate"]!["cells"]![0]!.AsObject();

        Assert.True(cell.Remove(propertyName));

        Assert.Throws<InvalidDataException>(
            () => AdmissionBenchmarkEvidence.ParseBundle(
                root.ToJsonString(),
                EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle));
    }

    [Theory]
    [InlineData("liveRequests")]
    [InlineData("liveWork")]
    [InlineData("liveOpens")]
    [InlineData("liveEffects")]
    [InlineData("liveWaiters")]
    [InlineData("drainSucceeded")]
    [InlineData("reopenSucceeded")]
    public void Missing_required_final_state_metric_is_refused_during_json_parsing(string propertyName)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(
            Bundle(),
            EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle))!.AsObject();

        JsonObject finalState = root["pairs"]![0]!["candidate"]!["finalState"]!.AsObject();

        Assert.True(finalState.Remove(propertyName));

        Assert.Throws<InvalidDataException>(
            () => AdmissionBenchmarkEvidence.ParseBundle(
                root.ToJsonString(),
                EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle));
    }

    [Theory]
    [InlineData("disposedAdmissions")]
    [InlineData("retainedBytesBeforeClose")]
    [InlineData("retainedBytesAfterClose")]
    [InlineData("closeNanoseconds")]
    [InlineData("drainSucceeded")]
    public void Missing_required_historical_churn_metric_is_refused_during_json_parsing(string propertyName)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(
            Bundle(),
            EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle))!.AsObject();

        JsonObject churn = root["pairs"]![0]!["candidate"]!["historicalChurn"]![0]!.AsObject();

        Assert.True(churn.Remove(propertyName));

        Assert.Throws<InvalidDataException>(
            () => AdmissionBenchmarkEvidence.ParseBundle(
                root.ToJsonString(),
                EvidenceTestJsonContext.Default.AdmissionBenchmarkEvidenceBundle));
    }

    private static AdmissionBenchmarkEvidenceBundle Bundle()
    {
        AdmissionBenchmarkPair[] pairs = new AdmissionBenchmarkPair[6];

        for (int index = 0; index < pairs.Length; index++)
        {
            string first = index % 2 == 0 ? "B" : "C";

            string second = index % 2 == 0 ? "C" : "B";

            pairs[index] = new(
                index,
                first,
                second,
                Run("B", index, first == "B" ? 0 : 1),
                Run("C", index, first == "C" ? 0 : 1));
        }

        return new("session", pairs);
    }

    private static AdmissionBenchmarkRevisionRun Run(
        string role,
        int pair,
        int order) =>
        new(
            role == "B"
                ? "1111111111111111111111111111111111111111"
                : "2222222222222222222222222222222222222222",
            true,
            "session",
            pair,
            order,
            role,
            "qualification",
            Inputs(role),
            new(
                "osx-arm64",
                "Arm64",
                "Arm64",
                "macOS 26",
                ".NET 10",
                "10.0.100",
                "CPU",
                8,
                1_000_000_000,
                false,
                false,
                false),
            Cells(),
            new(0, 0, 0, 0, 0, true, true),
            0)
        {
            HistoricalChurn =
            [
                new(0, 1, 1, 1, true),
                new(64, 1, 1, 1, true),
                new(640, 1, 1, 1, true),
            ],
        };

    private static AdmissionBenchmarkInputIdentity Inputs(string role)
    {
        AdmissionBenchmarkManifest manifest = AdmissionBenchmarkManifest.CreateDefault();

        const string gate = "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs";

        const string epoch = "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs";

        string catalogPath = Path.Combine(
            FindRepositoryRoot(),
            "tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt");

        AdmissionBenchmarkDigestEntry[] inputs = File.ReadAllLines(catalogPath)
            .Select<string, AdmissionBenchmarkDigestEntry>(
                line =>
                {
                    string path = line.Split('\t')[1];

                    if (path == epoch)
                    {
                        return new(path, role == "C" ? new string('c', 64) : string.Empty, role == "C");
                    }

                    return new(
                        path,
                        path == gate
                            ? role == "B" ? new string('a', 64) : new string('b', 64)
                            : new string('d', 64),
                        true);
                })
            .ToArray();

        string same = new('1', 64);

        return new(
            manifest.InputCatalogShapeDigest,
            same,
            same,
            same,
            same,
            same,
            same,
            same,
            same,
            inputs);
    }

    private static AdmissionBenchmarkCellResult[] Cells()
    {
        AdmissionBenchmarkProfile profile = AdmissionBenchmarkManifest.CreateDefault().Profiles[0];

        return AdmissionBenchmarkOperations.All.SelectMany(
            operation => new[]
            {
                Cell(operation, "one", 1, profile),
                Cell(operation, "two", 2, profile),
                Cell(operation, "eight", 8, profile),
                Cell(operation, "logical", 8, profile),
            }).ToArray();
    }

    private static AdmissionBenchmarkCellResult Cell(
        string operation,
        string concurrency,
        int workers,
        AdmissionBenchmarkProfile profile) =>
        new(
            operation,
            concurrency,
            workers,
            profile.WarmupIterations,
            profile.LatencySampleCount,
            checked((long)profile.LatencySampleCount * profile.LatencyBundleSize),
            profile.ThroughputIterations,
            100,
            100,
            100,
            100,
            checked(8L * profile.ThroughputIterations),
            profile.ThroughputIterations,
            8,
            0,
            0,
            AdmissionBenchmarkExpected.OperationCount(profile),
            0,
            AdmissionBenchmarkExpected.Checksum(profile, operation, workers),
            0);

    private static AdmissionBenchmarkEvidenceBundle Mutate(
        AdmissionBenchmarkEvidenceBundle evidence,
        string breakName)
    {
        AdmissionBenchmarkPair first = evidence.Pairs[0];

        AdmissionBenchmarkRevisionRun candidate = first.Candidate;

        candidate = breakName switch
        {
            "dirty" => candidate with { CleanTree = false },
            "non-commit" => candidate with { Revision = "branch-name" },
            "session" => candidate with { SessionId = "different" },
            "harness" => candidate with { Inputs = candidate.Inputs with { HarnessDigest = "different" } },
            "manifest" => candidate with { Inputs = candidate.Inputs with { ManifestDigest = "different" } },
            "project" => candidate with { Inputs = candidate.Inputs with { ProjectDigest = "different" } },
            "lock" => candidate with { Inputs = candidate.Inputs with { LockfileDigest = "different" } },
            "toolchain" => candidate with { Inputs = candidate.Inputs with { ToolchainDigest = "different" } },
            "native" => candidate with { Inputs = candidate.Inputs with { NativeBinaryDigest = "different" } },
            "environment" => candidate with { Environment = candidate.Environment with { CpuIdentity = "different" } },
            "outside-source" => candidate with { Inputs = candidate.Inputs with { Inputs = candidate.Inputs.Inputs.Select(static source => source.Path.StartsWith("src/RetroDownfall.Arcanum.Core/", StringComparison.Ordinal) && source.Path.EndsWith(".cs", StringComparison.Ordinal) ? source with { Digest = "different" } : source).ToArray() } },
            "duplicate-source" => candidate with { Inputs = candidate.Inputs with { Inputs = [.. candidate.Inputs.Inputs, candidate.Inputs.Inputs[0]] } },
            "gate-absent" => candidate with { Inputs = candidate.Inputs with { Inputs = candidate.Inputs.Inputs.Select(static source => source.Path.EndsWith("GrimoireConnectionAdmissionGate.cs", StringComparison.Ordinal) ? source with { Present = false, Digest = string.Empty } : source).ToArray() } },
            "crash" => candidate with { ExitCode = 2 },
            "cancel" => candidate with { ExitCode = 130 },
            "leak" => candidate with { FinalState = candidate.FinalState with { LiveWork = 1 } },
            "historical-churn" => candidate with { HistoricalChurn = [] },
            "candidate-source" => candidate with { Inputs = candidate.Inputs with { Inputs = candidate.Inputs.Inputs.Select(static source => source.Path.EndsWith("GrimoireConnectionAdmissionGate.cs", StringComparison.Ordinal) ? source with { Digest = "different-candidate" } : source).ToArray() } },
            "catalog-shape" => candidate with { Inputs = candidate.Inputs with { CatalogShapeDigest = new string('2', 64) } },
            "immutable-content" => candidate with { Inputs = candidate.Inputs with { ImmutableContentDigest = new string('2', 64) } },
            "malformed-digest" => candidate with { Inputs = candidate.Inputs with { Inputs = [candidate.Inputs.Inputs[0] with { Digest = new string('G', 64) }, .. candidate.Inputs.Inputs[1..]] } },
            "unsorted-input" => candidate with { Inputs = candidate.Inputs with { Inputs = candidate.Inputs.Inputs.Reverse().ToArray() } },
            "absolute-input" => candidate with { Inputs = candidate.Inputs with { Inputs = [candidate.Inputs.Inputs[0] with { Path = "/absolute" }, .. candidate.Inputs.Inputs[1..]] } },
            "missing-input" => candidate with { Inputs = candidate.Inputs with { Inputs = candidate.Inputs.Inputs[1..] } },
            "extra-input" => candidate with { Inputs = candidate.Inputs with { Inputs = [.. candidate.Inputs.Inputs, new("zz-extra", new string('e', 64), true)] } },
            "null-input-map" => candidate with { Inputs = candidate.Inputs with { Inputs = null! } },
            _ => candidate,
        };

        AdmissionBenchmarkPair[] pairs = [.. evidence.Pairs];

        pairs[0] = first with { Candidate = candidate };

        return evidence with { Pairs = pairs };
    }

    private static string FindRepositoryRoot() =>
        global::RetroDownfall.Arcanum.Tests.Support.TestRepositoryPaths.RepositoryRoot();
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AdmissionBenchmarkEvidenceBundle))]
internal sealed partial class EvidenceTestJsonContext : JsonSerializerContext
{
}
