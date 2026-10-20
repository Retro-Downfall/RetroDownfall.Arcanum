using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.PromptCache.Benchmarks;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PromptCacheBenchmarkManifest))]
[JsonSerializable(typeof(PromptCacheCorpusCase[]))]
[JsonSerializable(typeof(PromptCacheBenchmarkReport))]
[JsonSerializable(typeof(PromptCacheBenchmarkVerdict))]
public partial class PromptCacheBenchmarkJsonContext : JsonSerializerContext;
