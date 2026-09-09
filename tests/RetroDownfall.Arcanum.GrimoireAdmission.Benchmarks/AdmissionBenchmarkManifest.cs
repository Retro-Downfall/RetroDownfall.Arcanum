using System.Text.Json;

using System.Text.Json.Serialization.Metadata;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

internal sealed record AdmissionBenchmarkManifest(
    int SchemaVersion,
    string[] Operations,
    AdmissionBenchmarkConcurrency[] Concurrency,
    AdmissionBenchmarkProfile[] Profiles,
    string MaterialImprovementOperation,
    string MaterialImprovementConcurrency,
    AdmissionBenchmarkThresholds Thresholds,
    AdmissionBenchmarkBootstrap Bootstrap,
    string InputCatalogShapeDigest,
    string[] SourceDifferenceAllowlist)
{
    private const string ExactInputCatalogShapeDigest = "f3379e03364f8abc45b18e9abe1940997bc8ffa718a40668f9189bd6fadafb34";

    private static readonly string[] ExactAllowlist =
    [
        "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs",
        "src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs",
    ];

    internal static AdmissionBenchmarkManifest Parse(
        string json,
        JsonTypeInfo<AdmissionBenchmarkManifest> typeInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        AdmissionBenchmarkManifest manifest;

        try
        {
            manifest = JsonSerializer.Deserialize(json, typeInfo)
                ?? throw new InvalidDataException("The benchmark manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The benchmark manifest is not valid closed-schema JSON.", exception);
        }

        manifest.Validate();

        return manifest;
    }

    internal static AdmissionBenchmarkManifest CreateDefault() =>
        new(
            1,
            [.. AdmissionBenchmarkOperations.All],
            [
                new("one", 1),
                new("two", 2),
                new("eight", 8),
                new("logical", 0),
            ],
            [
                new(
                    "qualification",
                    20_000,
                    32,
                    2_000,
                    1_000_000,
                    900,
                    [
                        "request.finite",
                        "request.quiesceable",
                        "work.db-only",
                        "work.effect",
                        "open.failed",
                        "request.open",
                        "generation.read",
                        "request.finite",
                    ]),
                new(
                    "smoke",
                    8,
                    4,
                    4,
                    32,
                    120,
                    [
                        "request.finite",
                        "work.effect",
                        "open.failed",
                        "request.open",
                    ]),
            ],
            "ordinary.mixed",
            "logical",
            new(1.05, 1.10, 1.05, 1.05, 1.20),
            new("xorshift64star", 0x8A5CD789635D2DFFUL, 10_000, 0.05),
            ExactInputCatalogShapeDigest,
            [.. ExactAllowlist]);

    internal void Validate()
    {
        if (SchemaVersion != 1
            || Operations is null
            || !Operations.SequenceEqual(AdmissionBenchmarkOperations.All, StringComparer.Ordinal)
            || Operations.Distinct(StringComparer.Ordinal).Count() != Operations.Length)
        {
            throw new InvalidDataException("The manifest operation schema is not the closed version 1 contract.");
        }

        if (Concurrency is null
            || Concurrency.Length != 4
            || Concurrency.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != 4
            || Concurrency[0] != new AdmissionBenchmarkConcurrency("one", 1)
            || Concurrency[1] != new AdmissionBenchmarkConcurrency("two", 2)
            || Concurrency[2] != new AdmissionBenchmarkConcurrency("eight", 8)
            || Concurrency[3] != new AdmissionBenchmarkConcurrency("logical", 0))
        {
            throw new InvalidDataException("The manifest concurrency schema is invalid.");
        }

        if (Profiles is null
            || Profiles.Select(static profile => profile.Name).Distinct(StringComparer.Ordinal).Count() != 2
            || Profiles.Select(static profile => profile.Name).ToArray() is not ["qualification", "smoke"]
            || Profiles.Any(static profile => profile.WarmupIterations <= 0
                || profile.LatencyBundleSize <= 0
                || profile.LatencySampleCount <= 0
                || profile.ThroughputIterations <= 0
                || profile.MaximumDurationSeconds <= 0
                || profile.MixedSchedule is null
                || profile.MixedSchedule.Length == 0
                || profile.MixedSchedule.Any(operation => !AdmissionBenchmarkOperations.All.Contains(operation, StringComparer.Ordinal))))
        {
            throw new InvalidDataException("The manifest profiles are invalid.");
        }

        if (MaterialImprovementOperation != "ordinary.mixed"
            || MaterialImprovementConcurrency != "logical"
            || Thresholds is null
            || !PositiveFinite(Thresholds.SingleThreadP50MaximumRatio)
            || !PositiveFinite(Thresholds.P99MaximumRatio)
            || !PositiveFinite(Thresholds.EfMaximumRatio)
            || !PositiveFinite(Thresholds.EfAllocationMaximumRatio)
            || !PositiveFinite(Thresholds.MixedThroughputMinimumRatio))
        {
            throw new InvalidDataException("The manifest thresholds are invalid.");
        }

        if (Bootstrap is null
            || Bootstrap.Algorithm != "xorshift64star"
            || Bootstrap.Seed == 0
            || Bootstrap.ReplicateCount <= 0
            || !PositiveFinite(Bootstrap.LowerQuantile)
            || Bootstrap.LowerQuantile >= 1)
        {
            throw new InvalidDataException("The bootstrap contract is invalid.");
        }

        if (SourceDifferenceAllowlist is null
            || InputCatalogShapeDigest != ExactInputCatalogShapeDigest
            || !SourceDifferenceAllowlist.SequenceEqual(ExactAllowlist, StringComparer.Ordinal)
            || SourceDifferenceAllowlist.Any(static path => path.Contains('*')
                || path.EndsWith("/", StringComparison.Ordinal)
                || !path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The source difference allowlist is invalid.");
        }
    }

    private static bool PositiveFinite(double value) => value > 0 && double.IsFinite(value);
}
