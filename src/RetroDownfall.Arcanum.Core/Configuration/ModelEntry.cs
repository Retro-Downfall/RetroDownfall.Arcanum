using System.Text.Json;
using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// A single model advertised by a provider under <see cref="ProviderSettings.Models"/>. Carries
/// Scrying (vision) capability alongside the model name so a provider can mix vision-capable and
/// text-only models in the same <c>models</c> array.
/// </summary>
[JsonConverter(typeof(ModelEntryJsonConverter))]
public sealed record ModelEntry(string Name, bool SupportsVision = false)
{

    /// <summary>
    /// Implicit conversion from a bare model name — mirrors the JSON string-or-object back-compat
    /// form so collection-expression literals (<c>Models = ["gpt-4o"]</c>) keep compiling unchanged
    /// wherever vision capability is not being declared inline.
    /// </summary>
    public static implicit operator ModelEntry(string name) => new(name);

}

/// <summary>
/// AOT-safe converter accepting either a bare JSON string (<c>"gpt-4o"</c>, back-compat form —
/// <see cref="ModelEntry.SupportsVision"/> defaults to <c>false</c>) or an object
/// (<c>{ "name": "gpt-4o", "supportsVision": true }</c>). Writes are always the object form so a
/// round-tripped configuration file preserves the declared capability.
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
                    else
                    {
                        reader.Skip();
                    }
                }

                return new ModelEntry(name, supportsVision);

            default:
                throw new JsonException(
                    $"Provider 'models' entries must be a string or an object with 'name'/'supportsVision' (got {reader.TokenType}).");
        }
    }

    public override void Write(Utf8JsonWriter writer, ModelEntry value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("name", value.Name);

        writer.WriteBoolean("supportsVision", value.SupportsVision);

        writer.WriteEndObject();
    }
}
