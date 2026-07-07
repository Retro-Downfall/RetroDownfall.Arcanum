using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// AOT-safe helper for parsing JSON Schemas into a strongly-typed representation, validating
/// JSON payloads against them, and converting them to GBNF grammars for provider-side constrained
/// decoding. Uses only <see cref="JsonDocument"/> and hand-rolled traversal — no reflection.
/// </summary>
public static class JsonSchemaHelper
{

    /// <summary>
    /// Parses a JSON Schema document into a <see cref="JsonSchemaDefinition"/>.
    /// </summary>
    /// <param name="schema">The JSON Schema document.</param>
    /// <param name="maxDepth">Maximum allowed nesting depth. Defaults to 10; clamped 1–50.</param>
    /// <returns>
    /// A successful result containing the parsed schema, or a failure with
    /// <see cref="ErrorCodes.StructuredOutput.SchemaInvalid"/> when the schema is malformed or too deep.
    /// </returns>
    public static Result<JsonSchemaDefinition> Parse(JsonDocument schema, int maxDepth = 10)
    {

        int clampedDepth = Math.Clamp(maxDepth, 1, 50);

        try
        {

            JsonSchemaDefinition definition = ParseElement(schema.RootElement, clampedDepth, currentDepth: 0);

            return Result<JsonSchemaDefinition>.Success(definition);

        }
        catch (SchemaException ex)
        {

            return Result<JsonSchemaDefinition>.Failure(
                new Error(ErrorCodes.StructuredOutput.SchemaInvalid, ex.Message));

        }

    }

    /// <summary>
    /// Validates a JSON string against a parsed schema.
    /// </summary>
    /// <param name="json">The JSON payload to validate.</param>
    /// <param name="schema">The parsed schema definition.</param>
    /// <param name="maxDepth">Maximum allowed nesting depth for the payload.</param>
    /// <returns>A <see cref="ValidationResult"/> with validation errors, if any.</returns>
    public static ValidationResult Validate(string json, JsonSchemaDefinition schema, int maxDepth = 10)
    {

        if (string.IsNullOrWhiteSpace(json))
        {

            return new ValidationResult(false, ["JSON payload is empty."]);

        }

        int clampedDepth = Math.Clamp(maxDepth, 1, 50);

        try
        {

            using JsonDocument document = JsonDocument.Parse(json);

            List<string> errors = [];

            ValidateElement(document.RootElement, schema, "$", clampedDepth, 0, errors);

            return new ValidationResult(errors.Count == 0, errors);

        }
        catch (JsonException ex)
        {

            return new ValidationResult(false, [$"JSON parse error: {ex.Message}"]);

        }
        catch (SchemaException ex)
        {

            return new ValidationResult(false, [ex.Message]);

        }

    }

    /// <summary>
    /// Converts a parsed JSON Schema into a GBNF grammar string that llama.cpp can use for
    /// constrained decoding. Supports a pragmatic subset of JSON Schema; unsupported features are
    /// ignored and logged by the caller via the optional <paramref name="unsupportedWarning"/> callback.
    /// </summary>
    /// <param name="schema">The parsed schema definition.</param>
    /// <param name="maxDepth">Maximum allowed nesting depth for the generated grammar.</param>
    /// <returns>
    /// A successful result containing the GBNF grammar string, or a failure with
    /// <see cref="ErrorCodes.StructuredOutput.SchemaInvalid"/> when the schema is too deep or cannot be expressed.
    /// </returns>
    public static Result<string> ToGbnf(JsonSchemaDefinition schema, int maxDepth = 10)
    {

        int clampedDepth = Math.Clamp(maxDepth, 1, 50);

        try
        {

            GbnfBuilder builder = new();

            builder.DefineRule("root", ConvertToGbnf(schema, "root", clampedDepth, 0, builder));

            return Result<string>.Success(builder.BuildGrammar());

        }
        catch (SchemaException ex)
        {

            return Result<string>.Failure(
                new Error(ErrorCodes.StructuredOutput.SchemaInvalid, ex.Message));

        }

    }

    /// <summary>
    /// Reports the set of JSON Schema features supported and ignored by <see cref="ToGbnf"/>.
    /// Supported: object (properties, required, additionalProperties:false), string, number,
    /// integer, boolean, array (items), enum. Not supported: anyOf, oneOf, allOf, $ref, pattern,
    /// format, minimum/maximum, minLength/maxLength, additional schema composition.
    /// </summary>
    [SuppressMessage("Design", "CA1034:Nested types should not be visible", Justification = "Public for documentation and testability.")]
    public static class GbnfSupportedFeatures
    {

        public static IReadOnlyList<string> Supported =>
        [
            "object",
            "string",
            "number",
            "integer",
            "boolean",
            "array",
            "enum",
            "required",
            "additionalProperties:false"
        ];

        public static IReadOnlyList<string> NotSupported =>
        [
            "anyOf",
            "oneOf",
            "allOf",
            "$ref",
            "pattern",
            "format",
            "minimum",
            "maximum",
            "minLength",
            "maxLength",
            "uniqueItems",
            "multipleOf"
        ];

    }

    private static JsonSchemaDefinition ParseElement(JsonElement element, int maxDepth, int currentDepth)
    {

        if (currentDepth > maxDepth)
        {

            throw new SchemaException("schema exceeds maximum nesting depth");

        }

        if (element.ValueKind != JsonValueKind.Object)
        {

            throw new SchemaException("schema root must be a JSON object");

        }

        (string type, bool isNullable) = ExtractType(element);

        JsonSchemaDefinition definition = new()
        {

            Type = type,

            IsNullable = isNullable

        };

        if (element.TryGetProperty("properties", out JsonElement propertiesElement)
            && propertiesElement.ValueKind == JsonValueKind.Object)
        {

            foreach (JsonProperty property in propertiesElement.EnumerateObject())
            {

                definition.Properties[property.Name] =
                    ParseElement(property.Value, maxDepth, currentDepth + 1);

            }

        }

        if (element.TryGetProperty("required", out JsonElement requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {

            foreach (JsonElement required in requiredElement.EnumerateArray())
            {

                if (required.ValueKind == JsonValueKind.String)
                {

                    string? name = required.GetString();

                    if (!string.IsNullOrWhiteSpace(name))
                    {

                        definition.Required.Add(name);

                    }

                }

            }

        }

        if (element.TryGetProperty("items", out JsonElement itemsElement)
            && itemsElement.ValueKind == JsonValueKind.Object)
        {

            definition = definition with
            {

                Items = ParseElement(itemsElement, maxDepth, currentDepth + 1)

            };

        }

        if (element.TryGetProperty("enum", out JsonElement enumElement)
            && enumElement.ValueKind == JsonValueKind.Array)
        {

            foreach (JsonElement enumValue in enumElement.EnumerateArray())
            {

                definition.Enum.Add(enumValue.Clone());

            }

        }

        if (element.TryGetProperty("additionalProperties", out JsonElement additionalPropertiesElement))
        {

            if (additionalPropertiesElement.ValueKind == JsonValueKind.False)
            {

                definition = definition with { AdditionalProperties = false };

            }
            else if (additionalPropertiesElement.ValueKind == JsonValueKind.True)
            {

                definition = definition with { AdditionalProperties = true };

            }

        }

        return definition;

    }

    private static (string Type, bool IsNullable) ExtractType(JsonElement element)
    {

        if (!element.TryGetProperty("type", out JsonElement typeElement))
        {

            return (string.Empty, false);

        }

        if (typeElement.ValueKind == JsonValueKind.String)
        {

            string value = typeElement.GetString()?.ToLowerInvariant() ?? string.Empty;

            return (value, false);

        }

        if (typeElement.ValueKind == JsonValueKind.Array)
        {

            bool hasNull = false;

            string? firstNonNull = null;

            foreach (JsonElement type in typeElement.EnumerateArray())
            {

                if (type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? value = type.GetString()?.ToLowerInvariant();

                if (value is null)
                {
                    continue;
                }

                if (value == "null")
                {
                    hasNull = true;
                }
                else if (firstNonNull is null)
                {
                    firstNonNull = value;
                }

            }

            if (firstNonNull is not null)
            {
                return (firstNonNull, hasNull);
            }

            if (hasNull)
            {
                return ("null", false);
            }

        }

        return ("object", false);

    }

    private static void ValidateElement(
        JsonElement element,
        JsonSchemaDefinition schema,
        string path,
        int maxDepth,
        int currentDepth,
        List<string> errors)
    {

        if (currentDepth > maxDepth)
        {

            throw new SchemaException("JSON payload exceeds maximum nesting depth");

        }

        if (element.ValueKind == JsonValueKind.Null && schema.IsNullable)
        {

            return;

        }

        if (!string.IsNullOrEmpty(schema.Type) && !IsTypeMatch(element, schema.Type))
        {

            errors.Add($"{path}: expected type '{schema.Type}' but got '{GetJsonValueKindName(element.ValueKind)}'.");

            return;

        }

        if (schema.Enum.Count > 0)
        {

            foreach (JsonElement enumValue in schema.Enum)
            {

                if (JsonElementsEqual(element, enumValue))
                {

                    return;

                }

            }

            errors.Add($"{path}: value does not match any enum value.");

            return;

        }

        if (element.ValueKind == JsonValueKind.Object)
        {

            ValidateObject(element, schema, path, maxDepth, currentDepth, errors);

        }
        else if (element.ValueKind == JsonValueKind.Array)
        {

            ValidateArray(element, schema, path, maxDepth, currentDepth, errors);

        }

    }

    private static void ValidateObject(
        JsonElement element,
        JsonSchemaDefinition schema,
        string path,
        int maxDepth,
        int currentDepth,
        List<string> errors)
    {

        HashSet<string> seenProperties = [];

        foreach (JsonProperty property in element.EnumerateObject())
        {

            seenProperties.Add(property.Name);

            if (schema.Properties.TryGetValue(property.Name, out JsonSchemaDefinition? propertySchema))
            {

                ValidateElement(
                    property.Value,
                    propertySchema,
                    $"{path}.{property.Name}",
                    maxDepth,
                    currentDepth + 1,
                    errors);

            }
            else if (schema.AdditionalProperties == false)
            {

                errors.Add($"{path}: additional property '{property.Name}' is not allowed.");

            }

        }

        foreach (string required in schema.Required)
        {

            if (!seenProperties.Contains(required))
            {

                errors.Add($"{path}: required property '{required}' is missing.");

            }

        }

    }

    private static void ValidateArray(
        JsonElement element,
        JsonSchemaDefinition schema,
        string path,
        int maxDepth,
        int currentDepth,
        List<string> errors)
    {

        if (schema.Items is null)
        {

            return;

        }

        int index = 0;

        foreach (JsonElement item in element.EnumerateArray())
        {

            ValidateElement(item, schema.Items, $"{path}[{index}]", maxDepth, currentDepth + 1, errors);

            index++;

        }

    }

    private static bool IsTypeMatch(JsonElement element, string expectedType)
    {

        return expectedType.ToLowerInvariant() switch
        {

            "object" => element.ValueKind == JsonValueKind.Object,
            "array" => element.ValueKind == JsonValueKind.Array,
            "string" => element.ValueKind == JsonValueKind.String,
            "number" => element.ValueKind == JsonValueKind.Number,
            "integer" => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out _),
            "boolean" => element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => element.ValueKind == JsonValueKind.Null,
            _ => true

        };

    }

    private static string GetJsonValueKindName(JsonValueKind kind) => kind switch
    {

        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Undefined => "undefined",
        _ => kind.ToString().ToLowerInvariant()

    };

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {

        if (left.ValueKind != right.ValueKind)
        {

            return false;

        }

        return left.ValueKind switch
        {

            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetDecimal() == right.GetDecimal(),
            JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText()

        };

    }

    private static string ConvertToGbnf(
        JsonSchemaDefinition schema,
        string ruleName,
        int maxDepth,
        int currentDepth,
        GbnfBuilder builder)
    {

        if (currentDepth > maxDepth)
        {

            throw new SchemaException("schema exceeds maximum nesting depth for GBNF generation");

        }

        if (schema.Enum.Count > 0)
        {

            return builder.DefineRule(
                ruleName,
                string.Join(" | ", schema.Enum.Select(EscapeGbnfLiteral)));

        }

        return schema.Type.ToLowerInvariant() switch
        {

            "object" => ConvertObjectToGbnf(schema, ruleName, maxDepth, currentDepth, builder),
            "array" => ConvertArrayToGbnf(schema, ruleName, maxDepth, currentDepth, builder),
            "string" => DefineNullableRule(builder, ruleName, "string", schema.IsNullable),
            "number" or "integer" => DefineNullableRule(builder, ruleName, "number", schema.IsNullable),
            "boolean" => DefineNullableRule(builder, ruleName, "boolean", schema.IsNullable),
            "null" => builder.DefineRule(ruleName, "null"),
            _ => DefineNullableRule(builder, ruleName, "value", schema.IsNullable)

        };

    }

    private static string ConvertObjectToGbnf(
        JsonSchemaDefinition schema,
        string ruleName,
        int maxDepth,
        int currentDepth,
        GbnfBuilder builder)
    {

        if (schema.Properties.Count == 0)
        {

            return DefineNullableRule(builder, ruleName, "\"{\" ws \"}\"", schema.IsNullable);

        }

        // Build the grammar from the last property inward so each property is optional but,
        // when present, may be followed by a comma and the next declared property. This
        // preserves schema-declared order; required properties are still enforced by the
        // post-response validator (Phase 1a).
        List<KeyValuePair<string, JsonSchemaDefinition>> properties = schema.Properties.ToList();

        string remainder = "ws \"}\"";

        for (int i = properties.Count - 1; i >= 0; i--)
        {

            (string propertyName, JsonSchemaDefinition propertySchema) = properties[i];

            string keyRule = builder.DefineKeyRule(propertyName);

            string valueRule = ConvertToGbnf(
                propertySchema,
                $"{ruleName}_{propertyName}",
                maxDepth,
                currentDepth + 1,
                builder);

            System.Text.StringBuilder part = new();

            part.Append(keyRule).Append(" ws \":\" ws ").Append(valueRule);

            if (i < properties.Count - 1)
            {

                part.Append(" (ws \",\" ws ").Append(remainder).Append(")?");

            }

            remainder = part.ToString();

        }

        return DefineNullableRule(builder, ruleName, "\"{\" ws (" + remainder + ")? ws \"}\"", schema.IsNullable);

    }

    private static string ConvertArrayToGbnf(
        JsonSchemaDefinition schema,
        string ruleName,
        int maxDepth,
        int currentDepth,
        GbnfBuilder builder)
    {

        if (schema.Items is null)
        {

            return DefineNullableRule(builder, ruleName, "\"[\" ws \"]\"", schema.IsNullable);

        }

        string itemRule = ConvertToGbnf(
            schema.Items,
            $"{ruleName}_item",
            maxDepth,
            currentDepth + 1,
            builder);

        return DefineNullableRule(
            builder,
            ruleName,
            $"\"[\" ws ({itemRule} (ws \",\" ws {itemRule})*)? ws \"]\"", schema.IsNullable);

    }

    private static string EscapeGbnfLiteral(JsonElement element)
    {

        return element.ValueKind switch
        {

            JsonValueKind.String => GbnfBuilder.EscapeJsonStringLiteral(element.GetString() ?? string.Empty),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => GbnfBuilder.EscapeJsonStringLiteral(element.GetRawText())

        };

    }

    private static string DefineNullableRule(GbnfBuilder builder, string ruleName, string expression, bool nullable)
    {

        if (!nullable)
        {
            return builder.DefineRule(ruleName, expression);
        }

        string nonNullName = builder.DefineRule($"{ruleName}_nonnull", expression);

        return builder.DefineRule(ruleName, $"{nonNullName} | \"null\"");

    }

    private sealed class GbnfBuilder
    {

        private readonly Dictionary<string, string> _rules = [];

        public string DefineRule(string name, string expression)
        {

            string sanitizedName = SanitizeRuleName(name);

            if (!_rules.TryGetValue(sanitizedName, out string? existing))
            {

                _rules[sanitizedName] = expression;

                return sanitizedName;

            }

            if (string.Equals(existing, expression, StringComparison.Ordinal))
            {

                return sanitizedName;

            }

            int counter = 2;

            string candidate;

            do
            {

                candidate = $"{sanitizedName}_{counter}";

                counter++;

            } while (_rules.TryGetValue(candidate, out string? candidateExisting)
                && !string.Equals(candidateExisting, expression, StringComparison.Ordinal));

            if (!_rules.ContainsKey(candidate))
            {

                _rules[candidate] = expression;

            }

            return candidate;

        }

        public string DefineKeyRule(string key)
        {

            return DefineRule($"key_{key}", EscapeJsonStringLiteral($"\"{key}\""));

        }

        public string BuildGrammar()
        {

            List<string> lines =
            [
                "ws ::= [ \\t\\n\\r]*",
                "string ::= \"\\\"\" char* \"\\\"\"",
                "char ::= [^\"\\\\] | \"\\\\\" (\"\\\"\" | \"\\\\\" | \"/\" | \"b\" | \"f\" | \"n\" | \"r\" | \"t\" | \"u\" [0-9a-fA-F]{4})",
                "number ::= \"-\"? [0-9]+ (\".\" [0-9]+)? ([eE] [+-]? [0-9]+)?",
                "boolean ::= \"true\" | \"false\"",
                "null ::= \"null\"",
                "object ::= \"{\" ws (string ws \":\" ws value (ws \",\" ws string ws \":\" ws value)*)? ws \"}\"",
                "array ::= \"[\" ws (value (ws \",\" ws value)*)? ws \"]\"",
                "value ::= object | array | string | number | boolean | null"
            ];

            foreach ((string name, string expression) in _rules)
            {

                lines.Add($"{name} ::= {expression}");

            }

            return string.Join("\n", lines);

        }

        public static string EscapeJsonStringLiteral(string value)
        {

            System.Text.StringBuilder builder = new();

            builder.Append('"');

            foreach (char c in value)
            {

                builder.Append(c switch
                {

                    '\\' => "\\\\",
                    '"' => "\\\"",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ => c.ToString()

                });

            }

            builder.Append('"');

            return builder.ToString();

        }

        private static string SanitizeRuleName(string name)
        {

            ReadOnlySpan<char> span = name.AsSpan();

            System.Text.StringBuilder builder = new();

            foreach (char c in span)
            {

                if (char.IsLetterOrDigit(c) || c == '_')
                {

                    builder.Append(c);

                }
                else
                {

                    builder.Append('_');

                }

            }

            string sanitized = builder.Length == 0 ? "rule" : builder.ToString();

            if (char.IsDigit(sanitized[0]))
            {

                sanitized = "_" + sanitized;

            }

            return sanitized;

        }

    }

    private sealed class SchemaException : Exception
    {

        public SchemaException(string message) : base(message)
        {

        }

    }

}

/// <summary>
/// Result of validating a JSON payload against a <see cref="JsonSchemaDefinition"/>.
/// </summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);
