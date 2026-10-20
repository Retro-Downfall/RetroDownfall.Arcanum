using System.Text.Json;

namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

internal static class PromptCacheBenchmarkManifestReader
{
    internal const string ExpectedDigest = "ca2b386d4bc6bb76df055c8275fdcae50c5b508f8cc4cc698a891502707a2aa2";

    internal static (PromptCacheBenchmarkManifest Manifest, string Digest, string CorpusDigest) Load()
    {
        using Stream stream = typeof(PromptCacheBenchmarkManifestReader).Assembly.GetManifestResourceStream("RetroDownfall.Arcanum.PromptCache.Benchmarks.prompt-cache-workload-v1.json") ?? throw new InvalidDataException("manifestMissing");

        using StreamReader reader = new(stream);

        string json = reader.ReadToEnd();

        string digest = PromptCacheBenchmarkEvidence.Digest(json);

        var manifest = JsonSerializer.Deserialize(json, PromptCacheBenchmarkJsonContext.Default.PromptCacheBenchmarkManifest) ?? throw new InvalidDataException("manifestMissing");

        if (digest != ExpectedDigest || !PromptCacheBenchmarkEvidence.ValidateManifest(manifest)) throw new InvalidDataException("manifestIntegrityFailed");

        return (manifest, digest, PromptCacheBenchmarkEvidence.Digest(JsonSerializer.Serialize(manifest.Cases, PromptCacheBenchmarkJsonContext.Default.PromptCacheCorpusCaseArray)));
    }
}
