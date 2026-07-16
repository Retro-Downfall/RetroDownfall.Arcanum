using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// A strongly-typed, AOT-safe representation of a JSON Schema used for structured-output validation.
/// Built by <see cref="JsonSchemaHelper.Parse"/> from a <see cref="JsonDocument"/> without reflection.
/// </summary>
public sealed record JsonSchemaDefinition
{

    public string Type { get; init; } = string.Empty;

    public bool IsNullable { get; init; }

    public Dictionary<string, JsonSchemaDefinition> Properties { get; init; } = [];

    public HashSet<string> Required { get; init; } = [];

    public JsonSchemaDefinition? Items { get; init; }

    public List<JsonElement> Enum { get; init; } = [];

    public bool? AdditionalProperties { get; init; }

}
