using System.Collections.Immutable;

using System.Text.Json;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

public static class MemoryEvaluationDeterministicEvaluator
{
    private static readonly Error InvalidEvaluation = new(ErrorCodes.MemoryEvaluation.InvalidEvaluation, "The deterministic evaluation could not produce valid evidence.");

    // Keep shared type/property/item validation, while requiring enum and structural constraints
    // conjunctively. The existing helper returns early after a matching enum value.
    private static JsonSchemaDefinition WithoutEnums(JsonSchemaDefinition schema) => schema with
    {
        Enum = [],

        Properties = schema.Properties.ToDictionary(pair => pair.Key, pair => WithoutEnums(pair.Value), StringComparer.Ordinal),

        Items = schema.Items is null ? null : WithoutEnums(schema.Items)
    };

    // The general helper omits empty/whitespace required names. They are legal JSON keys,
    // so restore the complete accepted declaration before applying shared validation.
    private static JsonSchemaDefinition PreserveRequiredNames(JsonElement source, JsonSchemaDefinition parsed) => parsed with
    {
        Required = source.TryGetProperty("required", out JsonElement required)
            ? required.EnumerateArray().Select(name => name.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [],

        Properties = parsed.Properties.ToDictionary(
            pair => pair.Key,
            pair => PreserveRequiredNames(source.GetProperty("properties").GetProperty(pair.Key), pair.Value),
            StringComparer.Ordinal),

        Items = parsed.Items is null ? null : PreserveRequiredNames(source.GetProperty("items"), parsed.Items)
    };

    private static bool EnumsMatch(JsonElement value, JsonSchemaDefinition schema)
    {
        if (schema.Enum.Count > 0 && !schema.Enum.Any(candidate => JsonElement.DeepEquals(value, candidate)))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (schema.Properties.TryGetValue(property.Name, out JsonSchemaDefinition? child) && !EnumsMatch(property.Value, child))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.Items is { } items)
        {
            return value.EnumerateArray().All(item => EnumsMatch(item, items));
        }

        return true;
    }

    public static Result<ImmutableArray<MemoryEvaluationVerdict>> Evaluate(string answer, ImmutableArray<MemoryEvaluationCheck> checks, CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || !MemoryEvaluationValidator.Bounded(answer, MemoryEvaluationLimits.RetainedTextBytes)
                || !MemoryEvaluationValidator.ValidateChecks(checks))
            {
                return InvalidEvaluation;
            }

            ImmutableArray<MemoryEvaluationVerdict>.Builder verdicts = ImmutableArray.CreateBuilder<MemoryEvaluationVerdict>(checks.Length);

            foreach (MemoryEvaluationCheck check in checks)
            {
                ct.ThrowIfCancellationRequested();

                bool passed;

                if (check is MemoryEvaluationRegexCheck regex)
                {
                    passed = Regex.IsMatch(answer, regex.Pattern, RegexOptions.CultureInvariant | (regex.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), MemoryEvaluationLimits.RegexTimeout) == regex.ShouldMatch;
                }
                else
                {
                    MemoryEvaluationJsonSchemaCheck schema = (MemoryEvaluationJsonSchemaCheck)check;

                    using JsonDocument document = JsonDocument.Parse(schema.Schema.GetRawText());

                    Result<JsonSchemaDefinition> parsed = JsonSchemaHelper.Parse(document, MemoryEvaluationLimits.SchemaDepth);

                    JsonSchemaDefinition definition = PreserveRequiredNames(schema.Schema, parsed.Value);

                    passed = JsonSchemaHelper.Validate(answer, WithoutEnums(definition), MemoryEvaluationLimits.SchemaDepth).IsValid;

                    if (passed)
                    {
                        using JsonDocument payload = JsonDocument.Parse(answer);

                        passed = EnumsMatch(payload.RootElement, definition);
                    }
                }

                ct.ThrowIfCancellationRequested();

                verdicts.Add(passed ? MemoryEvaluationVerdict.Pass : MemoryEvaluationVerdict.Fail);
            }

            return verdicts.MoveToImmutable();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or RegexMatchTimeoutException or OperationCanceledException or OverflowException)
        {
            return InvalidEvaluation;
        }
    }
}
