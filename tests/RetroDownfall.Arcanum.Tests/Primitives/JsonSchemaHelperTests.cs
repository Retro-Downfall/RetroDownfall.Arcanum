using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Primitives;

public sealed class JsonSchemaHelperTests
{

    [Fact]
    public void Parse_SimpleObjectSchema_ReturnsDefinition()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age": { "type": "integer" }
              },
              "required": ["name"]
            }
            """);

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsSuccess);

        JsonSchemaDefinition definition = result.Value;

        Assert.Equal("object", definition.Type);

        Assert.Contains("name", definition.Properties.Keys);

        Assert.Contains("age", definition.Properties.Keys);

        Assert.Equal("string", definition.Properties["name"].Type);

        Assert.Equal("integer", definition.Properties["age"].Type);

        Assert.Contains("name", definition.Required);

    }

    [Fact]
    public void Parse_NestedSchema_ReturnsDefinition()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "person": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" }
                  },
                  "required": ["name"]
                }
              }
            }
            """);

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsSuccess);

        Assert.True(result.Value.Properties.ContainsKey("person"));

        Assert.Equal("object", result.Value.Properties["person"].Type);

        Assert.True(result.Value.Properties["person"].Properties.ContainsKey("name"));

    }

    [Fact]
    public void Parse_ArraySchema_ReturnsItemsDefinition()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "array",
              "items": { "type": "string" }
            }
            """);

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsSuccess);

        Assert.Equal("array", result.Value.Type);

        Assert.NotNull(result.Value.Items);

        Assert.Equal("string", result.Value.Items!.Type);

    }

    [Fact]
    public void Parse_EnumSchema_PreservesEnumValues()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "string",
              "enum": ["red", "green", "blue"]
            }
            """);

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsSuccess);

        Assert.Equal(3, result.Value.Enum.Count);

    }

    [Fact]
    public void Parse_DeeplyNestedSchema_ExceedsMaxDepth_ReturnsSchemaInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "a": {
                  "type": "object",
                  "properties": {
                    "b": {
                      "type": "object",
                      "properties": {
                        "c": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
            """);

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema, maxDepth: 2);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.StructuredOutput.SchemaInvalid, result.Error.Code);

    }

    [Fact]
    public void Validate_ValidJson_ReturnsValid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" },
                "age": { "type": "integer" }
              },
              "required": ["name"]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""
            {"name": "Alice", "age": 30}
            """, parsed.Value);

        Assert.True(result.IsValid);

        Assert.Empty(result.Errors);

    }

    [Fact]
    public void Validate_MissingRequired_ReturnsInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" }
              },
              "required": ["name"]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""{"age": 30}""", parsed.Value);

        Assert.False(result.IsValid);

        Assert.Contains(result.Errors, e => e.Contains("required property 'name' is missing", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Validate_WrongType_ReturnsInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "age": { "type": "integer" }
              }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""{"age": "thirty"}""", parsed.Value);

        Assert.False(result.IsValid);

        Assert.Contains(result.Errors, e => e.Contains("expected type 'integer'", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Validate_AdditionalPropertiesFalse_ReturnsInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" }
              },
              "additionalProperties": false
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""{"name": "Alice", "extra": 1}""", parsed.Value);

        Assert.False(result.IsValid);

        Assert.Contains(result.Errors, e => e.Contains("additional property 'extra' is not allowed", StringComparison.OrdinalIgnoreCase));

    }

    [Fact]
    public void Validate_ArrayWithItems_ReturnsInvalidForMismatchedItem()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "array",
              "items": { "type": "integer" }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""[1, 2, "three"]""", parsed.Value);

        Assert.False(result.IsValid);

        Assert.Contains(result.Errors, e => e.Contains("[2]", StringComparison.Ordinal));

    }

    [Fact]
    public void Validate_EnumMismatch_ReturnsInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "string",
              "enum": ["red", "green"]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate(""""blue"""", parsed.Value);

        Assert.False(result.IsValid);

    }

    [Fact]
    public void Validate_PayloadExceedsMaxDepth_ReturnsInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "a": {
                  "type": "object",
                  "properties": {
                    "b": { "type": "string" }
                  }
                }
              }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""{"a":{"b":{"c":"d"}}}""", parsed.Value, maxDepth: 2);

        Assert.False(result.IsValid);

    }

    [Fact]
    public void ToGbnf_SimpleObject_ReturnsGrammarWithRootRule()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string" }
              }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        Result<string> grammar = JsonSchemaHelper.ToGbnf(parsed.Value);

        Assert.True(grammar.IsSuccess);

        Assert.Contains("root ::=", grammar.Value, StringComparison.Ordinal);

        Assert.Contains("key_name", grammar.Value, StringComparison.Ordinal);

    }

    [Fact]
    public void ToGbnf_NestedObject_ReturnsGrammarWithNestedRule()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "person": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string" }
                  }
                }
              }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        Result<string> grammar = JsonSchemaHelper.ToGbnf(parsed.Value);

        Assert.True(grammar.IsSuccess);

        Assert.Contains("root_person", grammar.Value, StringComparison.Ordinal);

    }

    [Fact]
    public void ToGbnf_Enum_ReturnsLiteralAlternatives()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "string",
              "enum": ["red", "green"]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        Result<string> grammar = JsonSchemaHelper.ToGbnf(parsed.Value);

        Assert.True(grammar.IsSuccess);

        Assert.Contains("\"red\" | \"green\"", grammar.Value, StringComparison.Ordinal);

    }

    [Fact]
    public void ToGbnf_DeepSchema_ExceedsMaxDepth_ReturnsSchemaInvalid()
    {

        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "a": {
                  "type": "object",
                  "properties": {
                    "b": {
                      "type": "object",
                      "properties": {
                        "c": { "type": "string" }
                      }
                    }
                  }
                }
              }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        Result<string> grammar = JsonSchemaHelper.ToGbnf(parsed.Value, maxDepth: 2);

        Assert.True(grammar.IsFailure);

        Assert.Equal(ErrorCodes.StructuredOutput.SchemaInvalid, grammar.Error.Code);

    }

    [Fact]
    public void SupportedFeatures_DocumentsObjectStringNumberBooleanArray()
    {

        IReadOnlyList<string> supported = JsonSchemaHelper.GbnfSupportedFeatures.Supported;

        Assert.Contains("object", supported);

        Assert.Contains("string", supported);

        Assert.Contains("number", supported);

        Assert.Contains("boolean", supported);

        Assert.Contains("array", supported);

        Assert.Contains("enum", supported);

    }

    [Fact]
    public void NotSupportedFeatures_DocumentsCompositionAndRef()
    {

        IReadOnlyList<string> notSupported = JsonSchemaHelper.GbnfSupportedFeatures.NotSupported;

        Assert.Contains("anyOf", notSupported);

        Assert.Contains("oneOf", notSupported);

        Assert.Contains("$ref", notSupported);

        Assert.Contains("pattern", notSupported);

    }

}
