using System.Collections.Immutable;

using System.Text;

using System.Text.Json;

using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Core.Annals;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;

public static class MemoryEvaluationValidator
{
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static readonly Error InvalidRequest = new(ErrorCodes.MemoryEvaluation.InvalidRequest, "The memory evaluation request violates policy v1.");

    public static Result<MemoryEvaluationValidatedRequest> Validate(MemoryEvaluationRequest request)
    {
        try
        {
            if (request is null || request.Scope is null || request.Options is null || request.Workload is null || request.Memory is null
                || request.EvaluationId == Guid.Empty || !Enum.IsDefined(request.Scope.Kind)
                || (request.Scope.Kind == MemoryEvaluationScopeKind.Campaign ? request.Scope.CampaignId is null || request.Scope.CampaignId == Guid.Empty : request.Scope.CampaignId is not null)
                || !Identifier(request.ProviderName) || !Identifier(request.Model)
                || request.PairCount is < MemoryEvaluationLimits.MinPairs or > MemoryEvaluationLimits.MaxPairs
                || request.Options.MaxOutputTokens is < MemoryEvaluationLimits.MinOutputTokens or > MemoryEvaluationLimits.MaxOutputTokens
                || request.Options.Temperature is { } temperature && (!double.IsFinite(temperature) || temperature is < 0 or > 2)
                || request.Options.TopP is { } topP && (!double.IsFinite(topP) || topP is < 0 or > 1)
                || request.Options.ReasoningEffort is { } effort && !Enum.IsDefined(effort)
                || !Bounded(request.Workload.SystemPrompt, MemoryEvaluationLimits.SystemPromptBytes)
                || !Bounded(request.Workload.UserPrompt, MemoryEvaluationLimits.UserPromptBytes)
                || request.Memory.Covenant.IsDefault || request.Memory.Saga.IsDefault || request.Memory.Lexicon.IsDefault)
            {
                return InvalidRequest;
            }

            int targets = checked(request.Memory.Covenant.Length + request.Memory.Saga.Length + request.Memory.Lexicon.Length);

            if (targets is < MemoryEvaluationLimits.MinTargets or > MemoryEvaluationLimits.MaxTargets)
            {
                return InvalidRequest;
            }

            HashSet<(MemoryEvaluationSource Store, string SubjectId, string VersionId)> identities = [];

            foreach (MemoryEvaluationCovenantTarget target in request.Memory.Covenant)
            {
                if (target is null || target.EntryId == Guid.Empty || target.VersionId == Guid.Empty || !target.FragmentDigest.IsValid
                    || !identities.Add((MemoryEvaluationSource.Covenant, target.EntryId.ToString("D"), target.VersionId.ToString("D"))))
                {
                    return InvalidRequest;
                }
            }

            foreach (MemoryEvaluationSagaTarget target in request.Memory.Saga)
            {
                if (target is null || !StoreId(target.MemoryId) || !VersionId(target.VersionId)
                    || target.HashFormat != AnnalContentHashFormat.LegacyStoreDigest || !target.ContentDigest.IsValid
                    || !identities.Add((MemoryEvaluationSource.Saga, target.MemoryId, target.VersionId)))
                {
                    return InvalidRequest;
                }
            }

            foreach (MemoryEvaluationLexiconTarget target in request.Memory.Lexicon)
            {
                if (target is null || target.EntryId == Guid.Empty || !VersionId(target.VersionId)
                    || !Enum.IsDefined(target.HashFormat) || !target.ContentDigest.IsValid
                    || !identities.Add((MemoryEvaluationSource.Lexicon, target.EntryId.ToString("D"), target.VersionId)))
                {
                    return InvalidRequest;
                }
            }

            if (!ValidateChecks(request.Checks))
            {
                return InvalidRequest;
            }

            // Rebuild retained collections and JSON so borrowed ImmutableArray storage and documents
            // cannot rewrite the already validated identity through a retained caller-owned reference.
            MemoryEvaluationRequest frozen = request with
            {
                Memory = new(request.Memory.Covenant.ToArray().ToImmutableArray(), request.Memory.Saga.ToArray().ToImmutableArray(), request.Memory.Lexicon.ToArray().ToImmutableArray()),
                Checks = request.Checks.Select(check => check is MemoryEvaluationJsonSchemaCheck schema ? new MemoryEvaluationJsonSchemaCheck(schema.Schema) : check).ToImmutableArray()
            };

            return new MemoryEvaluationValidatedRequest(frozen);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidOperationException or OverflowException)
        {
            return InvalidRequest;
        }
    }

    internal static bool Bounded(string value, int bytes) => value is not null && StrictUtf8.GetByteCount(value) <= bytes;

    private static bool Identifier(string value) => !string.IsNullOrWhiteSpace(value) && Bounded(value, MemoryEvaluationLimits.RequestBytes) && !value.Any(char.IsControl);

    // Saga subjects are opaque durable store IDs, not assumed to be generated GUIDs.
    private static bool StoreId(string value) => !string.IsNullOrWhiteSpace(value) && Bounded(value, MemoryEvaluationLimits.RequestBytes) && !value.Any(char.IsControl);

    private static bool VersionId(string value) => StoreId(value);

    internal static bool ValidateChecks(ImmutableArray<MemoryEvaluationCheck> checks)
    {
        if (checks.IsDefaultOrEmpty || checks.Length > MemoryEvaluationLimits.MaxChecks)
        {
            return false;
        }

        int bytes = 0;

        HashSet<string> identities = new(StringComparer.Ordinal);

        foreach (MemoryEvaluationCheck check in checks)
        {
            switch (check)
            {
                case MemoryEvaluationRegexCheck regex when Bounded(regex.Pattern, MemoryEvaluationLimits.RegexBytes):
                    _ = new Regex(regex.Pattern, RegexOptions.CultureInvariant | (regex.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), MemoryEvaluationLimits.RegexTimeout);

                    bytes = checked(bytes + StrictUtf8.GetByteCount(regex.Pattern) + 2);

                    break;

                case MemoryEvaluationJsonSchemaCheck schema when ValidateSchema(schema.Schema, 0):
                    bytes = checked(bytes + StrictUtf8.GetByteCount(schema.Schema.GetRawText()));

                    using (JsonDocument document = JsonDocument.Parse(schema.Schema.GetRawText()))
                    {
                        if (JsonSchemaHelper.Parse(document, MemoryEvaluationLimits.SchemaDepth).IsFailure)
                        {
                            return false;
                        }
                    }

                    break;

                default:
                    return false;
            }

            if (bytes > MemoryEvaluationLimits.CheckBytes || !identities.Add(MemoryEvaluationDigests.CheckIdentity(check)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateSchema(JsonElement schema, int depth)
    {
        if (depth >= MemoryEvaluationLimits.SchemaDepth || schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonProperty property in schema.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }

            switch (property.Name)
            {
                case "type":
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        if (!SupportedType(property.Value.GetString()))
                        {
                            return false;
                        }
                    }
                    else if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0
                        || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || !SupportedType(item.GetString()))
                        || property.Value.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() != property.Value.GetArrayLength())
                    {
                        return false;
                    }

                    break;

                case "properties":
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        return false;
                    }

                    HashSet<string> propertyNames = new(StringComparer.Ordinal);

                    foreach (JsonProperty child in property.Value.EnumerateObject())
                    {
                        if (!propertyNames.Add(child.Name) || !ValidateSchema(child.Value, depth + 1))
                        {
                            return false;
                        }
                    }

                    break;

                case "items":
                    if (!ValidateSchema(property.Value, depth + 1))
                    {
                        return false;
                    }

                    break;

                case "required":
                    if (property.Value.ValueKind != JsonValueKind.Array
                        || property.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                        || property.Value.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() != property.Value.GetArrayLength())
                    {
                        return false;
                    }

                    break;

                case "enum":
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0)
                    {
                        return false;
                    }

                    break;

                case "additionalProperties":
                    if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return false;
                    }

                    break;

                default:
                    return false;
            }
        }

        return true;
    }

    private static bool SupportedType(string? type) => type is "object" or "array" or "string" or "number" or "integer" or "boolean" or "null";
}
