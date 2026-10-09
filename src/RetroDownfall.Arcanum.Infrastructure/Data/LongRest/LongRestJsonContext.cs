using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.LongRest;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LongRestSnapshot))]
internal sealed partial class LongRestJsonContext : JsonSerializerContext;
