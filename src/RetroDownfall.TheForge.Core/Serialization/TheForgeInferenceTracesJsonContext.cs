using System.Text.Json.Serialization;
using RetroDownfall.TheForge.Core.Models.Traces;

namespace RetroDownfall.TheForge.Core.Serialization;

/// <summary>Source-generated JSON for The Forge-local inference trace history.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InferenceTraceStoreDocument))]
[JsonSerializable(typeof(InferenceTraceRecord))]
[JsonSerializable(typeof(InferenceTraceEventRecord))]
[JsonSerializable(typeof(IReadOnlyList<InferenceTraceRecord>))]
[JsonSerializable(typeof(IReadOnlyList<InferenceTraceEventRecord>))]
public partial class TheForgeInferenceTracesJsonContext : JsonSerializerContext;
