using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The only path from operator text to an FTS5 MATCH expression or a fallback LIKE pattern.
/// </summary>
/// <remarks>
/// The compiler emits every operator itself. Each term becomes a quoted FTS literal with its
/// embedded double quotes doubled, the prefix marker is appended outside the closing quote, and the
/// terms are joined with an explicit <c>AND</c>. Nothing the caller typed can therefore become
/// syntax: an input of <c>a OR b</c> searches for the three literal tokens, and <c>*</c> or
/// <c>NEAR(</c> are ordinary characters inside a quoted literal.
///
/// <para>Text safety reuses the pinned policy-v1 rules rather than a second, looser set. A query
/// that could contain a Format code point or a lone surrogate would let two visually identical
/// searches produce different filter digests and therefore different cursors.</para>
/// </remarks>
internal sealed class CovenantSearchQueryCompiler
{

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly SearchValues<char> LikeMetacharacters = SearchValues.Create(['%', '_', '\\']);

    public Result<CovenantCompiledSearchTerms> Compile(string query)
    {

        ArgumentNullException.ThrowIfNull(query);

        int byteCount;

        try
        {

            byteCount = StrictUtf8.GetByteCount(query);

        }
        catch (EncoderFallbackException)
        {

            return new Error(
                "Validation.InvalidQuery",
                "A Covenant search query must contain valid Unicode scalar values.");

        }

        if (byteCount > CovenantLimits.MaxSearchQueryBytes)
        {

            return new Error(
                "Validation.InvalidQuery",
                $"A Covenant search query cannot exceed {CovenantLimits.MaxSearchQueryBytes} strict UTF-8 bytes.");

        }

        Result scalars = ValidateScalars(query);

        if (scalars.IsFailure)
        {

            return scalars.Error;

        }

        string normalized;

        try
        {

            normalized = CovenantUnicodePolicyV1.NormalizeToNfc(query);

        }
        catch (ArgumentException)
        {

            return new Error(
                "Validation.InvalidQuery",
                "A Covenant search query must contain valid Unicode scalar values.");

        }

        ImmutableArray<string> terms = SplitTerms(normalized);

        if (terms.IsEmpty)
        {

            return new Error("Validation.InvalidQuery", "A Covenant search query requires at least one term.");

        }

        if (terms.Length > CovenantLimits.MaxSearchQueryTerms)
        {

            return new Error(
                "Validation.InvalidQuery",
                $"A Covenant search query cannot exceed {CovenantLimits.MaxSearchQueryTerms} terms.");

        }

        StringBuilder match = new();

        ImmutableArray<string>.Builder patterns = ImmutableArray.CreateBuilder<string>(terms.Length);

        foreach (string term in terms)
        {

            if (match.Length > 0)
            {

                _ = match.Append(" AND ");

            }

            _ = match.Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append("\"*");

            patterns.Add($"%{EscapeLike(term)}%");

        }

        return new CovenantCompiledSearchTerms(match.ToString(), terms, patterns.MoveToImmutable());

    }

    private static Result ValidateScalars(string query)
    {

        ReadOnlySpan<char> remaining = query.AsSpan();

        while (!remaining.IsEmpty)
        {

            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);

            if (status != OperationStatus.Done)
            {

                return new Error(
                    "Validation.InvalidQuery",
                    "A Covenant search query must contain valid Unicode scalar values.");

            }

            int scalar = rune.Value;

            if (scalar == 0x00
                || (scalar is >= 0x00 and <= 0x1f && scalar is not 0x09 and not 0x0a and not 0x0d)
                || scalar is >= 0x7f and <= 0x9f)
            {

                return new Error(
                    "Validation.InvalidQuery",
                    "A Covenant search query contains a prohibited control scalar.");

            }

            if (CovenantUnicodePolicyV1.IsFormatScalar(scalar))
            {

                return new Error(
                    "Validation.InvalidQuery",
                    "A Covenant search query contains a Unicode Format scalar.");

            }

            remaining = remaining[consumed..];

        }

        return Result.Success();

    }

    /// <summary>
    /// Splits on the policy-v1 whitespace table, so two queries that differ only in exotic spacing
    /// compile to the same terms and therefore the same filter digest.
    /// </summary>
    private static ImmutableArray<string> SplitTerms(string normalized)
    {

        List<string> terms = [];

        StringBuilder current = new();

        ReadOnlySpan<char> remaining = normalized.AsSpan();

        while (!remaining.IsEmpty)
        {

            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int consumed);

            if (status != OperationStatus.Done)
            {

                return [];

            }

            if (CovenantUnicodePolicyV1.IsWhitespaceScalar(rune.Value))
            {

                if (current.Length > 0)
                {

                    terms.Add(current.ToString());

                    _ = current.Clear();

                }

            }
            else
            {

                _ = current.Append(remaining[..consumed]);

            }

            remaining = remaining[consumed..];

        }

        if (current.Length > 0)
        {

            terms.Add(current.ToString());

        }

        return [.. terms];

    }

    /// <summary>
    /// Escapes the three characters <c>LIKE</c> treats specially, under the explicit
    /// <c>ESCAPE '\'</c> clause the fallback query declares.
    /// </summary>
    private static string EscapeLike(string term)
    {

        if (!term.AsSpan().ContainsAny(LikeMetacharacters))
        {

            return term;

        }

        StringBuilder escaped = new(term.Length + 8);

        foreach (char character in term)
        {

            if (character is '%' or '_' or '\\')
            {

                _ = escaped.Append(CovenantCompiledSearchTerms.LikeEscape);

            }

            _ = escaped.Append(character);

        }

        return escaped.ToString();

    }

}
