using System.Text.Json;

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
    public void Identity_or_execution_drift_is_invalid_evidence(string breakName)
    {

        AdmissionBenchmarkEvidenceBundle evidence = Mutate(Bundle(), breakName);

        AdmissionBenchmarkEvidenceValidation validation = AdmissionBenchmarkEvidence.Validate(
            AdmissionBenchmarkManifest.CreateDefault(),
            evidence,
            baseIsAncestor: breakName != "reversed-ancestry");

        Assert.False(validation.Valid);

        Assert.Contains(validation.Errors, error => error.StartsWith(breakName, StringComparison.Ordinal));

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

    private static AdmissionBenchmarkInputIdentity Inputs(string role) =>
        new(
            "manifest",
            "harness",
            "project",
            "lock",
            "toolchain",
            "native-manifest",
            "native-binary",
            [
                new("Directory.Build.props", "same", true),
                new("src/RetroDownfall.Arcanum.Core/Core.cs", "same", true),
                new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs", role, true),
                new("src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs", role == "C" ? "candidate" : string.Empty, role == "C"),
            ]);

    private static AdmissionBenchmarkCellResult[] Cells() =>
        AdmissionBenchmarkOperations.All.SelectMany(
            static operation => new[]
            {
                new AdmissionBenchmarkCellResult(operation, "one", 1, 100, 100, 100, 100, 8, 0, 0, 1, 0, 1, 0),
                new AdmissionBenchmarkCellResult(operation, "two", 2, 100, 100, 100, 100, 8, 0, 0, 1, 0, 1, 0),
                new AdmissionBenchmarkCellResult(operation, "eight", 8, 100, 100, 100, 100, 8, 0, 0, 1, 0, 1, 0),
                new AdmissionBenchmarkCellResult(operation, "logical", 8, 100, 100, 100, 100, 8, 0, 0, 1, 0, 1, 0),
            }).ToArray();

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
            "outside-source" => candidate with { Inputs = candidate.Inputs with { CompiledSources = candidate.Inputs.CompiledSources.Select(static source => source.Path == "src/RetroDownfall.Arcanum.Core/Core.cs" ? source with { Digest = "different" } : source).ToArray() } },
            "duplicate-source" => candidate with { Inputs = candidate.Inputs with { CompiledSources = [.. candidate.Inputs.CompiledSources, candidate.Inputs.CompiledSources[0]] } },
            "gate-absent" => candidate with { Inputs = candidate.Inputs with { CompiledSources = candidate.Inputs.CompiledSources.Select(static source => source.Path.EndsWith("GrimoireConnectionAdmissionGate.cs", StringComparison.Ordinal) ? source with { Present = false, Digest = string.Empty } : source).ToArray() } },
            "crash" => candidate with { ExitCode = 2 },
            "cancel" => candidate with { ExitCode = 130 },
            "leak" => candidate with { FinalState = candidate.FinalState with { LiveWork = 1 } },
            "historical-churn" => candidate with { HistoricalChurn = [] },
            "candidate-source" => candidate with { Inputs = candidate.Inputs with { CompiledSources = candidate.Inputs.CompiledSources.Select(static source => source.Path.EndsWith("GrimoireConnectionAdmissionGate.cs", StringComparison.Ordinal) ? source with { Digest = "different-candidate" } : source).ToArray() } },
            _ => candidate,
        };

        AdmissionBenchmarkPair[] pairs = [.. evidence.Pairs];

        pairs[0] = first with { Candidate = candidate };

        return evidence with { Pairs = pairs };

    }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AdmissionBenchmarkEvidenceBundle))]
internal sealed partial class EvidenceTestJsonContext : JsonSerializerContext
{
}
