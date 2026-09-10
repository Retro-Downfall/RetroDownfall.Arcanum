using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AdmissionBenchmarkManifest))]
[JsonSerializable(typeof(AdmissionBenchmarkRevisionRun))]
[JsonSerializable(typeof(AdmissionBenchmarkEvidenceBundle))]
[JsonSerializable(typeof(AdmissionBenchmarkComparisonReport))]
internal sealed partial class AdmissionBenchmarkJsonContext : JsonSerializerContext
{
}

internal static class AdmissionBenchmarkManifestLoader
{
    internal static AdmissionBenchmarkManifest Load()
    {
        using Stream stream = typeof(AdmissionBenchmarkManifestLoader).Assembly
            .GetManifestResourceStream(
                "RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.grimoire-admission-workload-v1.json")
            ?? throw new InvalidOperationException("The embedded Grimoire-admission manifest is missing.");

        using StreamReader reader = new(stream);

        return AdmissionBenchmarkManifest.Parse(
            reader.ReadToEnd(),
            AdmissionBenchmarkJsonContext.Default.AdmissionBenchmarkManifest);
    }
}
