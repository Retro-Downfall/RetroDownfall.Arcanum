using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Intelligence.WebResearch;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PerplexityRequest))]
[JsonSerializable(typeof(PerplexityMessage))]
[JsonSerializable(typeof(PerplexityMessage[]))]
[JsonSerializable(typeof(PerplexityResponse))]
[JsonSerializable(typeof(PerplexityChoice))]
[JsonSerializable(typeof(PerplexityChoice[]))]
[JsonSerializable(typeof(PerplexitySearchResult))]
[JsonSerializable(typeof(PerplexitySearchResult[]))]
[JsonSerializable(typeof(PerplexityUsage))]
[JsonSerializable(typeof(PerplexityCost))]
[JsonSerializable(typeof(PerplexityErrorResponse))]
[JsonSerializable(typeof(PerplexityError))]
internal sealed partial class WebResearchJsonContext : JsonSerializerContext;
