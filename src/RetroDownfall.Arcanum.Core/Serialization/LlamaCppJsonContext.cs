using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.LlamaCpp;

namespace RetroDownfall.Arcanum.Core.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GgufModelManifest))]

public partial class LlamaCppJsonContext : JsonSerializerContext;
