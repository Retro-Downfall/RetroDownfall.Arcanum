using System.Text.Json.Serialization;
using System.Collections.Immutable;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MemoryEvaluationRegistration))]
[JsonSerializable(typeof(MemoryEvaluationSourceCoordinate))]
[JsonSerializable(typeof(MemoryEvaluationTerminalReport))]
[JsonSerializable(typeof(MemoryEvaluationStoredEvidence))]
[JsonSerializable(typeof(MemoryEvaluationAggregate))]
[JsonSerializable(typeof(MemoryEvaluationCheckpoint))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(PatternSnapshot))]
[JsonSerializable(typeof(DomainType))]

public partial class GrimoireJsonContext : JsonSerializerContext;
