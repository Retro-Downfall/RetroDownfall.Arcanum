using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Lexicon;

namespace RetroDownfall.Arcanum.Core.Serialization;

/// <summary>
/// AOT-safe source-generated JSON for The Lexicon. <c>LexiconEntryDto.Facts</c> is serialized as a
/// JSON string array into the <c>FactsJson</c> column, and the MCP <c>scribe_lexicon</c> payload
/// deserializes incoming fact arrays here. Never use reflection-based <c>JsonSerializer</c> overloads.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(LexiconEntryDto))]
[JsonSerializable(typeof(LexiconEntryDto[]))]
[JsonSerializable(typeof(List<LexiconEntryDto>))]
public partial class LexiconJsonContext : JsonSerializerContext;
