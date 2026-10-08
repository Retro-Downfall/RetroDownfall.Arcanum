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
    public void Validate_EnumWithNonStringType_StillValidatesEnum()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "enum": [1, 2, 3]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult valid = JsonSchemaHelper.Validate("2", parsed.Value);

        Assert.True(valid.IsValid);

        ValidationResult invalid = JsonSchemaHelper.Validate("99", parsed.Value);

        Assert.False(invalid.IsValid);
    }

    [Fact]
    public void Validate_NumericEnumEquality_HandlesDecimalPrecision()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "enum": [1.0, 2.5]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("1", parsed.Value);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MissingType_AcceptsAnyValue()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "properties": {
                "value": {}
              }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult stringResult = JsonSchemaHelper.Validate("""{"value": "anything"}""", parsed.Value);

        Assert.True(stringResult.IsValid);

        ValidationResult numberResult = JsonSchemaHelper.Validate("""{"value": 42}""", parsed.Value);

        Assert.True(numberResult.IsValid);
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

    /// <summary>
    /// A client-supplied structured-output schema can contain a numeric enum, and the model can
    /// return a magnitude outside <see cref="decimal"/>'s range. Comparing with GetDecimal threw
    /// FormatException straight out of Validate's documented ValidationResult contract.
    /// </summary>
    [Fact]
    public void Validate_NumericEnum_WithOutOfDecimalRangePayload_ReturnsResultInsteadOfThrowing()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": { "score": { "type": "number", "enum": [0.5, 1.0] } }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("""{"score": 1e30}""", parsed.Value);

        Assert.False(result.IsValid);
    }

    /// <summary>
    /// The schema side is equally client-controlled: an out-of-range enum literal must not throw
    /// when compared against an ordinary payload value.
    /// </summary>
    [Fact]
    public void Validate_OutOfDecimalRangeEnumLiteral_ReturnsResultInsteadOfThrowing()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "number",
              "enum": [1e30]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        Assert.False(JsonSchemaHelper.Validate("1", parsed.Value).IsValid);

        Assert.True(JsonSchemaHelper.Validate("1e30", parsed.Value).IsValid);
    }

    /// <summary>
    /// One error per failing element makes the report scale with the payload, and callers join that
    /// list into a public error envelope. A wholly wrong array must therefore report a bounded,
    /// readable summary rather than one line per element.
    /// </summary>
    [Fact]
    public void Validate_EveryElementOfALargeArrayFails_ReportsABoundedErrorList()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "array",
              "items": { "type": "string" }
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        string payload = "[" + string.Join(',', Enumerable.Range(0, 20_000)) + "]";

        ValidationResult result = JsonSchemaHelper.Validate(payload, parsed.Value);

        Assert.False(result.IsValid);

        Assert.InRange(result.Errors.Count, 1, JsonSchemaHelper.MaxReportedErrors + 1);

        Assert.True(
            string.Join("; ", result.Errors).Length < 64 * 1024,
            "The joined report is embedded verbatim in a public error envelope.");
    }

    /// <summary>
    /// The array case is bounded because every element recurses through the one guard in
    /// <c>ValidateElement</c>. The object case never recurses — one unexpected property is one
    /// <c>errors.Add</c> in a flat loop — so it needs its own guard to honour the same ceiling.
    /// </summary>
    [Fact]
    public void Validate_EveryPropertyOfALargeObjectIsUnexpected_ReportsABoundedErrorList()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        string payload = "{" + string.Join(',', Enumerable.Range(0, 20_000).Select(i => $"\"p{i}\":1")) + "}";

        ValidationResult result = JsonSchemaHelper.Validate(payload, parsed.Value);

        Assert.False(result.IsValid);

        Assert.InRange(result.Errors.Count, 1, JsonSchemaHelper.MaxReportedErrors + 1);

        Assert.True(
            string.Join("; ", result.Errors).Length < 64 * 1024,
            "The joined report is embedded verbatim in a public error envelope.");
    }

    /// <summary>
    /// A caller-supplied <c>required</c> array scales with the request rather than the model reply,
    /// so an unbounded missing-property report is the strongest amplification vector of the two.
    /// </summary>
    [Fact]
    public void Validate_EveryRequiredPropertyOfALargeSchemaIsMissing_ReportsABoundedErrorList()
    {
        string requiredNames = string.Join(',', Enumerable.Range(0, 20_000).Select(i => $"\"p{i}\""));

        using JsonDocument schema = JsonDocument.Parse($$"""
            {
              "type": "object",
              "required": [{{requiredNames}}]
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        ValidationResult result = JsonSchemaHelper.Validate("{}", parsed.Value);

        Assert.False(result.IsValid);

        Assert.InRange(result.Errors.Count, 1, JsonSchemaHelper.MaxReportedErrors + 1);

        Assert.True(
            string.Join("; ", result.Errors).Length < 64 * 1024,
            "The joined report is embedded verbatim in a public error envelope.");
    }

    /// <summary>
    /// Capping the unexpected-property report must not stop the walk that fills the seen-property
    /// set: a required property that appears after the ceiling is reached is still present, and
    /// reporting it as missing would tell the model to add a field it already sent.
    /// </summary>
    [Fact]
    public void Validate_TruncatedObjectReport_DoesNotInventMissingRequiredProperties()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": { "a": { "type": "integer" } },
              "required": ["a"],
              "additionalProperties": false
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        // "a" is last, so it is only seen well past the error ceiling.
        string payload = "{"
            + string.Join(',', Enumerable.Range(0, 20_000).Select(i => $"\"p{i}\":1"))
            + ",\"a\":1}";

        ValidationResult result = JsonSchemaHelper.Validate(payload, parsed.Value);

        Assert.InRange(result.Errors.Count, 1, JsonSchemaHelper.MaxReportedErrors + 1);

        Assert.DoesNotContain(result.Errors, e => e.Contains("required property 'a' is missing", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sentinel announces that the report was truncated, so it must not be appended to an
    /// object report that already lists everything that failed.
    /// </summary>
    [Fact]
    public void Validate_ObjectReportBelowTheCeiling_OmitsTheTruncationSentinel()
    {
        using JsonDocument schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """);

        Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(schema);

        Assert.True(parsed.IsSuccess);

        string payload = "{" + string.Join(',', Enumerable.Range(0, 3).Select(i => $"\"p{i}\":1")) + "}";

        ValidationResult result = JsonSchemaHelper.Validate(payload, parsed.Value);

        Assert.Equal(3, result.Errors.Count);

        Assert.DoesNotContain(result.Errors, e => e.Contains("Validation stopped", StringComparison.Ordinal));
    }

    /// <summary>
    /// A type array is a union: the first non-null member used to be the only one kept, so a payload
    /// matching a later member was rejected and a later unrecognized member was never seen.
    /// </summary>
    [Fact]
    public void Parse_TypeUnion_RecordsEveryDeclaredNonNullType()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":["string","integer","null"]}""");

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsSuccess);

        Assert.Equal("string", result.Value.Type);

        Assert.Equal(new[] { "integer" }, result.Value.AlternativeTypes);

        Assert.True(result.Value.IsNullable);
    }

    [Fact]
    public void Parse_NullableSingleType_HasNoAlternativeTypes()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":["string","null"]}""");

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsSuccess);

        Assert.Equal("string", result.Value.Type);

        Assert.Empty(result.Value.AlternativeTypes);

        Assert.True(result.Value.IsNullable);
    }

    [Fact]
    public void Validate_TypeUnion_AcceptsAnyDeclaredMemberAndRejectsTheRest()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":["string","integer","null"]}""");

        JsonSchemaDefinition definition = JsonSchemaHelper.Parse(schema).Value;

        Assert.True(JsonSchemaHelper.Validate("\"text\"", definition).IsValid);

        Assert.True(JsonSchemaHelper.Validate("5", definition).IsValid);

        Assert.True(JsonSchemaHelper.Validate("null", definition).IsValid);

        ValidationResult rejected = JsonSchemaHelper.Validate("true", definition);

        Assert.False(rejected.IsValid);

        string error = Assert.Single(rejected.Errors);

        Assert.Contains("'string'", error, StringComparison.Ordinal);

        Assert.Contains("'integer'", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A union member the validator does not recognize used to match every value, so one unknown
    /// member turned the whole union into "accept anything" on the structured-output and client-tool
    /// paths. An unknown name matches nothing; the recognized members still decide.
    /// </summary>
    [Fact]
    public void Validate_TypeUnionWithAnUnknownMember_DoesNotAcceptEveryValue()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":["string","strng"]}""");

        JsonSchemaDefinition definition = JsonSchemaHelper.Parse(schema).Value;

        Assert.True(JsonSchemaHelper.Validate("\"text\"", definition).IsValid);

        Assert.False(JsonSchemaHelper.Validate("{}", definition).IsValid);

        Assert.False(JsonSchemaHelper.Validate("5", definition).IsValid);
    }

    [Fact]
    public void Validate_UnknownSingleType_MatchesNoValue()
    {
        using JsonDocument schema = JsonDocument.Parse("""{"type":"strng"}""");

        JsonSchemaDefinition definition = JsonSchemaHelper.Parse(schema).Value;

        ValidationResult result = JsonSchemaHelper.Validate("\"text\"", definition);

        Assert.False(result.IsValid);

        Assert.Contains("'strng'", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    /// <summary>
    /// A malformed <c>type</c> declaration used to be rewritten rather than refused: a non-string
    /// union member was dropped and a number, object, boolean, null or empty-array <c>type</c> became
    /// <c>object</c>, so no caller ever saw the declaration the author actually wrote.
    /// </summary>
    [Theory]
    [InlineData("""{"type":["string",5]}""")]
    [InlineData("""{"type":["string",null]}""")]
    [InlineData("""{"type":7}""")]
    [InlineData("""{"type":[]}""")]
    [InlineData("""{"type":{}}""")]
    [InlineData("""{"type":true}""")]
    [InlineData("""{"type":null}""")]
    [InlineData("""{"type":"object","properties":{"a":{"type":[1]}}}""")]
    public void Parse_MalformedTypeDeclaration_IsRejectedAsInvalidSchema(string schemaJson)
    {
        using JsonDocument schema = JsonDocument.Parse(schemaJson);

        Result<JsonSchemaDefinition> result = JsonSchemaHelper.Parse(schema);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.StructuredOutput.SchemaInvalid, result.Error.Code);
    }
}
