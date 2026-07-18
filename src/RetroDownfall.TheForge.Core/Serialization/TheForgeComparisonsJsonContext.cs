using System.Text.Json.Serialization;
using RetroDownfall.TheForge.Core.Models.Comparisons;

namespace RetroDownfall.TheForge.Core.Serialization;

/// <summary>Source-generated JSON for The Forge-local Comparison Workbench history.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ComparisonStoreDocument))]
[JsonSerializable(typeof(ComparisonRunRecord))]
[JsonSerializable(typeof(ComparisonVariantResultRecord))]
[JsonSerializable(typeof(IReadOnlyList<ComparisonRunRecord>))]
[JsonSerializable(typeof(IReadOnlyList<ComparisonVariantResultRecord>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
public partial class TheForgeComparisonsJsonContext : JsonSerializerContext;
