using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Lexicon;

public sealed class LexiconCanonicalValue
{
    internal LexiconCanonicalValue(
        string name,
        string nameNormalized,
        string type,
        IReadOnlyList<string> facts,
        string factsJson,
        string factsText)
    {
        Name = name;

        NameNormalized = nameNormalized;

        Type = type;

        Facts = [.. facts];

        FactsJson = factsJson;

        FactsText = factsText;
    }

    public string Name { get; }

    public string NameNormalized { get; }

    public string Type { get; }

    public ImmutableArray<string> Facts { get; }

    public string FactsJson { get; }

    public string FactsText { get; }
}

/// <summary>
/// Produces the one canonical Lexicon value used by persistence, search, provenance, and digests.
/// </summary>
public static class LexiconValueNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static Result<LexiconCanonicalValue> NormalizeScribe(
        string? name,
        string? type,
        IReadOnlyList<string>? facts,
        string? currentType = null,
        IReadOnlyList<string>? currentFacts = null) =>
        Normalize(name, type, facts, currentType, currentFacts, correction: false);

    public static Result<LexiconCanonicalValue> NormalizeCorrection(
        string? name,
        string? type,
        IReadOnlyList<string>? facts) =>
        Normalize(name, type, facts, currentType: null, currentFacts: null, correction: true);

    private static Result<LexiconCanonicalValue> Normalize(
        string? name,
        string? type,
        IReadOnlyList<string>? facts,
        string? currentType,
        IReadOnlyList<string>? currentFacts,
        bool correction)
    {
        string canonicalName = name?.Trim() ?? string.Empty;

        if (canonicalName.Length == 0
            || canonicalName.Length > LexiconLimits.MaxNameLength
            || !IsStrictUtf8(canonicalName))
        {
            return new Error(
                ErrorCodes.Lexicon.InvalidName,
                $"A Lexicon entity name is required, must not exceed {LexiconLimits.MaxNameLength} characters, and must be valid Unicode.");
        }

        string canonicalType = ResolveType(type, currentType, correction);

        if (canonicalType.Length == 0
            || canonicalType.Length > LexiconLimits.MaxTypeLength
            || !IsStrictUtf8(canonicalType))
        {
            return InvalidValue(
                correction,
                $"A Lexicon entity type is required, must not exceed {LexiconLimits.MaxTypeLength} characters, and must be valid Unicode.");
        }

        List<string> canonicalFacts = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        if (currentFacts is not null)
        {
            Result currentResult = AddFacts(currentFacts, canonicalFacts, seen, correction);

            if (currentResult.IsFailure)
            {
                return currentResult.Error;
            }
        }

        if (facts is not null)
        {
            Result incomingResult = AddFacts(facts, canonicalFacts, seen, correction);

            if (incomingResult.IsFailure)
            {
                return incomingResult.Error;
            }
        }

        if (canonicalFacts.Count == 0)
        {
            return InvalidValue(correction, "A Lexicon value requires at least one non-empty fact.");
        }

        string normalizedName = canonicalName.ToUpperInvariant();

        if (!IsStrictUtf8(normalizedName))
        {
            return new Error(ErrorCodes.Lexicon.InvalidName, "The normalized Lexicon name is not valid Unicode.");
        }

        string[] factArray = [.. canonicalFacts];

        return Result<LexiconCanonicalValue>.Success(
            new LexiconCanonicalValue(
                canonicalName,
                normalizedName,
                canonicalType,
                factArray,
                JsonSerializer.Serialize(factArray, LexiconJsonContext.Default.StringArray),
                string.Join('\n', factArray)));
    }

    private static string ResolveType(string? type, string? currentType, bool correction)
    {
        string canonicalType = type?.Trim() ?? string.Empty;

        if (canonicalType.Length > 0 || correction)
        {
            return canonicalType;
        }

        string canonicalCurrentType = currentType?.Trim() ?? string.Empty;

        return canonicalCurrentType.Length > 0
            ? canonicalCurrentType
            : LexiconLimits.DefaultType;
    }

    private static Result AddFacts(
        IReadOnlyList<string> source,
        List<string> destination,
        HashSet<string> seen,
        bool correction)
    {
        foreach (string? fact in source)
        {
            string canonicalFact = fact?.Trim() ?? string.Empty;

            if (canonicalFact.Length == 0)
            {
                continue;
            }

            if (!IsStrictUtf8(canonicalFact))
            {
                return InvalidValue(correction, "Lexicon facts must be valid Unicode.");
            }

            if (seen.Add(canonicalFact))
            {
                destination.Add(canonicalFact);
            }
        }

        return Result.Success();
    }

    private static Error InvalidValue(bool correction, string message) =>
        new(
            correction ? ErrorCodes.Lexicon.InvalidReplacement : ErrorCodes.Lexicon.InvalidFact,
            message);

    private static bool IsStrictUtf8(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);

            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
