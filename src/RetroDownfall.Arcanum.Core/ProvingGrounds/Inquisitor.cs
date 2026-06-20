using System.Text.Json;

using System.Text.Json.Serialization;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]

[JsonDerivedType(typeof(RegexInquisitor), "regex")]

[JsonDerivedType(typeof(JsonSchemaInquisitor), "jsonSchema")]

[JsonDerivedType(typeof(SemanticInquisitor), "semantic")]

public abstract record Inquisitor
{

    public string? Label { get; init; }

}

public sealed record RegexInquisitor(string Pattern, bool ShouldMatch = true, bool IgnoreCase = false) : Inquisitor;

public sealed record JsonSchemaInquisitor(JsonElement Schema) : Inquisitor;

public sealed record SemanticInquisitor(string Question, bool ExpectedAnswer = true) : Inquisitor;
