using System.Text.Json;

using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Performance;

namespace RetroDownfall.Arcanum.Covenant.Benchmarks;

/// <summary>
/// Reads the pinned workload every Covenant benchmark number is produced against.
/// </summary>
/// <remarks>
/// Read from the embedded copy rather than from disk. A gate that read a manifest beside the binary
/// would let an operator move a ceiling by editing a file next to the executable, and a baseline
/// recorded under one workload would silently compare against a candidate measured under another.
///
/// <para>The shapes themselves live in <c>Core.Performance</c>. This host is outside the solution and
/// outside <c>dotnet test</c>, so anything declared here is invisible to every lane; the model, the
/// gate, the comparison, and the measurement loop are all somewhere the ordinary suite can reach
/// them, and what stays here is the file reading and the process orchestration.</para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(WorkloadManifest))]
[JsonSerializable(typeof(BenchmarkRun))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext
{
}

internal static class WorkloadManifestLoader
{

    internal static WorkloadManifest Load()
    {

        using Stream stream = typeof(WorkloadManifestLoader).Assembly
            .GetManifestResourceStream(
                "RetroDownfall.Arcanum.Covenant.Benchmarks.covenant-workload-v1.json")
            ?? throw new InvalidOperationException("The embedded workload manifest is missing from this build.");

        return JsonSerializer.Deserialize(stream, BenchmarkJsonContext.Default.WorkloadManifest)
            ?? throw new InvalidOperationException("The embedded workload manifest is empty.");

    }

}
