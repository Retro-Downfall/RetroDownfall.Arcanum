using System.Text.Json;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// AOT-safe helper for parsing JSON Schemas into a strongly-typed representation and validating
/// JSON payloads against them. Uses only <see cref="JsonDocument"/> and hand-rolled traversal —
/// no reflection.
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
    /// Ceiling on how many per-element errors one validation reports. Callers join the list into a
    /// public error envelope and into correction prompts, so a payload where every element fails
    /// must not turn into a report that scales with the payload.
    /// </summary>
    public const int MaxReportedErrors = 100;

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

            if (errors.Count >= MaxReportedErrors)
            {

                errors.Add($"Validation stopped after {MaxReportedErrors} errors; the payload may have more.");

            }

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
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // Defence in depth: numeric accessors on JsonElement throw for out-of-range literals.
            // Validate documents a ValidationResult return, so no numeric helper may escape it.
            return new ValidationResult(false, [$"JSON value error: {ex.Message}"]);

        }

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

        // Every recursion into an element goes through here, so one guard bounds the whole walk.
        if (errors.Count >= MaxReportedErrors)
        {

            return;

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
            JsonValueKind.Number => NumbersEqual(left, right),
            JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText()

        };

    }

    /// <summary>
    /// Compares two JSON numbers without throwing. <see cref="JsonElement.GetDecimal"/> raises
    /// <see cref="FormatException"/> for magnitudes outside <see cref="decimal"/>'s range (for
    /// example <c>1e30</c>), which would escape the documented <see cref="ValidationResult"/>
    /// contract; out-of-range operands fall back to literal comparison instead.
    /// </summary>
    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {

        if (left.TryGetDecimal(out decimal leftValue) && right.TryGetDecimal(out decimal rightValue))
        {

            return leftValue == rightValue;

        }

        return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

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
