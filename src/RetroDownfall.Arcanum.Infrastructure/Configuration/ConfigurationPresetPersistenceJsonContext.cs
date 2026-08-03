using System.Text.Json.Serialization;

using System.Collections.Immutable;

using RetroDownfall.Arcanum.Core.Configuration.Presets;

namespace RetroDownfall.Arcanum.Infrastructure.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]

[JsonSerializable(typeof(ConfigurationPresetProvenance))]

[JsonSerializable(typeof(ConfigurationPresetJournalDocument))]

internal partial class ConfigurationPresetPersistenceJsonContext : JsonSerializerContext;

internal sealed record ConfigurationPresetJournalDocument(
    string Operation,
    ImmutableArray<ConfigurationPresetBaselineValue> PreviousValues,
    ImmutableArray<ConfigurationPresetBaselineValue> CandidateValues,
    string PreviousValuesHash,
    string CandidateValuesHash,
    ConfigurationPresetProvenance? PreviousProvenance,
    ConfigurationPresetProvenance? NextProvenance,
    DateTimeOffset PreparedAt);
