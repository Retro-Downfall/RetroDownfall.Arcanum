using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.CommLink;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]

[JsonSerializable(typeof(WebhookPayloadDto))]

public partial class CommLinkInfrastructureJsonContext : JsonSerializerContext;
