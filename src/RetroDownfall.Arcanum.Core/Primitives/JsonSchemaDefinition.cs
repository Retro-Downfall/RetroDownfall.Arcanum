using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// A strongly-typed, AOT-safe representation of a JSON Schema used for structured-output validation.
/// Built by <see cref="JsonSchemaHelper.Parse"/> from a <see cref="JsonDocument"/> without reflection.
/// </summary>
public sealed record JsonSchemaDefinition
{
    public string Type { get; init; } = string.Empty;

    /// <summary>
    /// The other non-null members of a <c>type</c> array (<c>["string", "integer"]</c> keeps
    /// <c>string</c> in <see cref="Type"/> and <c>integer</c> here). A value satisfies the schema when
    /// it matches <see cref="Type"/> or any alternative; <c>null</c> is carried by <see cref="IsNullable"/>.
    /// </summary>
    public List<string> AlternativeTypes { get; init; } = [];

    public bool IsNullable { get; init; }

    public Dictionary<string, JsonSchemaDefinition> Properties { get; init; } = [];

    public HashSet<string> Required { get; init; } = [];

    public JsonSchemaDefinition? Items { get; init; }

    public List<JsonElement> Enum { get; init; } = [];

    public bool? AdditionalProperties { get; init; }
}
