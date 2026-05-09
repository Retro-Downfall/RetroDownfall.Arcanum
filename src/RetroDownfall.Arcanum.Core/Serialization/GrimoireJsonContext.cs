using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Pattern.Entities;

namespace RetroDownfall.Arcanum.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PatternSnapshot))]
[JsonSerializable(typeof(DomainType))]

public partial class GrimoireJsonContext : JsonSerializerContext;
