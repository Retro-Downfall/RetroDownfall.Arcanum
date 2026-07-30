using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Api.Models;

namespace RetroDownfall.Arcanum.Api.Serialization;

/// <summary>
/// Small AOT-safe JSON context for native web-tool results. Keeping these types separate lets the
/// tools strictly bound their own serialized envelopes before the generic tool materializer sees
/// them.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebToolCitation))]
[JsonSerializable(typeof(WebToolCitation[]))]
[JsonSerializable(typeof(WebToolUsage))]
[JsonSerializable(typeof(WebSearchToolData))]
[JsonSerializable(typeof(WebSearchToolResultEnvelope))]
[JsonSerializable(typeof(WebToolLink))]
[JsonSerializable(typeof(WebToolLink[]))]
[JsonSerializable(typeof(ReadUrlToolData))]
[JsonSerializable(typeof(ReadUrlToolResultEnvelope))]
internal sealed partial class WebToolJsonContext : JsonSerializerContext;
