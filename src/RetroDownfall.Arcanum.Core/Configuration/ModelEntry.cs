using System.Text.Json;
using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// A single model advertised by a provider under <see cref="ProviderSettings.Models"/>. Carries
/// Scrying (vision) capability alongside the model name so a provider can mix vision-capable and
/// text-only models in the same <c>models</c> array.
/// </summary>
/// <remarks>
/// Properties use <c>set</c> (not <c>init</c>) so the configuration binding source generator can
/// assign them — <c>init</c>-only properties are silently skipped (dotnet/runtime#107856).
/// </remarks>
[JsonConverter(typeof(ModelEntryJsonConverter))]
public sealed record ModelEntry
{

    public ModelEntry()
    {
    }

    public ModelEntry(
        string Name,
        bool SupportsVision = false,
        ReasoningWireDialect? WireDialect = null,
        int? MaxBudgetTokens = null)
    {

        this.Name = Name;

        this.SupportsVision = SupportsVision;

        this.WireDialect = WireDialect;

        this.MaxBudgetTokens = MaxBudgetTokens;

    }

    public string Name { get; set; } = string.Empty;

    public bool SupportsVision { get; set; }

    /// <summary>
    /// Optional wire-shape hint for nonstandard reasoning budgets. <see langword="null"/> means the
    /// standard OpenAI-compatible wire dialect is sufficient; set only for third-party endpoints
    /// that require <c>openRouter</c>, <c>topLevelReasoningBudget</c>, or <c>anthropicThinking</c>.
    /// The adapter enforces this at request time: a numeric budget requires a nonstandard dialect,
    /// and standard rejects numeric budgets.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReasoningWireDialect? WireDialect { get; set; }

    /// <summary>
    /// Optional per-single-turn reasoning budget ceiling (1–2,097,152 tokens). Only meaningful
    /// alongside a nonstandard wire dialect.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxBudgetTokens { get; set; }

    /// <summary>
    /// Implicit conversion from a bare model name — mirrors the JSON string-or-object back-compat
    /// form so collection-expression literals (<c>Models = ["gpt-4o"]</c>) keep compiling unchanged
    /// wherever vision capability is not being declared inline.
    /// </summary>
    public static implicit operator ModelEntry(string name) => new(name);

}

/// <summary>
/// AOT-safe converter accepting either a bare JSON string (<c>"gpt-4o"</c>, back-compat form —
/// <see cref="ModelEntry.SupportsVision"/> defaults to <c>false</c> and reasoning remains unconfigured)
/// or a factual object with `name`/`supportsVision` and optional `reasoning` (only `wireDialect` and
/// `maxBudgetTokens`). Writes are always the object form and include the reasoning object only when
/// it was declared.
/// </summary>
public sealed class ModelEntryJsonConverter : JsonConverter<ModelEntry>
{
    public override ModelEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new ModelEntry(reader.GetString() ?? string.Empty);

            case JsonTokenType.StartObject:
                string name = string.Empty;

                bool supportsVision = false;

                ReasoningWireDialect? wireDialect = null;

                int? maxBudgetTokens = null;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    string propertyName = reader.GetString() ?? string.Empty;

                    _ = reader.Read();

                    if (string.Equals(propertyName, "name", StringComparison.OrdinalIgnoreCase))
                    {
                        name = reader.GetString() ?? string.Empty;
                    }
                    else if (string.Equals(propertyName, "supportsVision", StringComparison.OrdinalIgnoreCase))
                    {
                        supportsVision = reader.TokenType is JsonTokenType.True or JsonTokenType.False
                            && reader.GetBoolean();
                    }
                    else if (string.Equals(propertyName, "reasoning", StringComparison.OrdinalIgnoreCase))
                    {
                        if (reader.TokenType == JsonTokenType.Null)
                        {
                            wireDialect = null;
                            maxBudgetTokens = null;
                        }
                        else if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName)
                                {
                                    continue;
                                }

                                string reasoningProperty = reader.GetString() ?? string.Empty;

                                _ = reader.Read();

                                if (string.Equals(reasoningProperty, "wireDialect", StringComparison.OrdinalIgnoreCase))
                                {
                                    wireDialect = reader.TokenType switch
                                    {
                                        JsonTokenType.String => JsonSerializer.Deserialize(
                                            ref reader,
                                            ConfigurationJsonContext.Default.ReasoningWireDialect),
                                        JsonTokenType.Null => null,
                                        _ => throw new JsonException(
                                            "Reasoning wireDialect must be a string enum value (standard, openRouter, topLevelReasoningBudget, anthropicThinking)."),
                                    };
                                }
                                else if (string.Equals(reasoningProperty, "maxBudgetTokens", StringComparison.OrdinalIgnoreCase))
                                {
                                    maxBudgetTokens = reader.TokenType == JsonTokenType.Number
                                        ? reader.GetInt32()
                                        : null;
                                }
                                else
                                {
                                    reader.Skip();
                                }
                            }
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                return new ModelEntry(name, supportsVision, wireDialect, maxBudgetTokens);

            default:
                throw new JsonException(
                    $"Provider 'models' entries must be a string or an object with 'name'/'supportsVision'/'reasoning' (got {reader.TokenType}).");
        }
    }

    public override void Write(Utf8JsonWriter writer, ModelEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("name", value.Name);

        writer.WriteBoolean("supportsVision", value.SupportsVision);

        if (value.WireDialect is not null || value.MaxBudgetTokens is not null)
        {
            writer.WritePropertyName("reasoning");

            writer.WriteStartObject();

            if (value.WireDialect is not null)
            {
                writer.WritePropertyName("wireDialect");

                JsonSerializer.Serialize(
                    writer,
                    value.WireDialect,
                    ConfigurationJsonContext.Default.ReasoningWireDialect);
            }

            if (value.MaxBudgetTokens is not null)
            {
                writer.WriteNumber("maxBudgetTokens", value.MaxBudgetTokens.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
