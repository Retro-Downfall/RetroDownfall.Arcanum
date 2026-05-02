using System.Text.Json.Serialization;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Pattern.Entities;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]

[JsonSerializable(typeof(ApiResponse<string>))]

[JsonSerializable(typeof(Result<string>))]

[JsonSerializable(typeof(Error))]

[JsonSerializable(typeof(PingRequest))]

[JsonSerializable(typeof(PatternSnapshot))]

[JsonSerializable(typeof(DomainType))]

[JsonSerializable(typeof(IntelligenceEventType))]

[JsonSerializable(typeof(IntelligenceEvent))]

public partial class ArcanumJsonContext : JsonSerializerContext;
