using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

public static class FtsMatchQuerySanitizer
{
    private static readonly HashSet<string> ReservedTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND",
        "OR",
        "NOT",
        "NEAR",
    };

    /// <summary>
    /// Reduces free text to a space-separated list of FTS5 bare words that cannot be read as query syntax.
    /// </summary>
    /// <remarks>
    /// Walks Unicode scalar values rather than UTF-16 code units, so a letter outside the Basic Multilingual Plane
    /// stays whole instead of being split into two surrogate halves that each look like a separator. A token is made of
    /// letters, digits, underscores and the combining marks that belong to them; a run of marks with nothing to
    /// attach to is dropped, because it tokenizes to nothing.
    /// </remarks>
    public static string Sanitize(string query)
    {
        StringBuilder builder = new(query.Length);

        StringBuilder currentToken = new();

        bool tokenHasBase = false;

        foreach (Rune rune in query.EnumerateRunes())
        {
            if (IsBaseCharacter(rune))
            {
                tokenHasBase = true;

                _ = currentToken.Append(rune);
            }
            else if (IsCombiningMark(rune))
            {
                _ = currentToken.Append(rune);
            }
            else
            {
                AppendToken(builder, currentToken, tokenHasBase);

                tokenHasBase = false;
            }
        }

        AppendToken(builder, currentToken, tokenHasBase);

        return builder.ToString();
    }

    private static bool IsBaseCharacter(Rune rune) =>
        rune.Value == '_' || Rune.IsLetterOrDigit(rune);

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static void AppendToken(StringBuilder builder, StringBuilder currentToken, bool tokenHasBase)
    {
        string token = currentToken.ToString();

        currentToken.Clear();

        if (token.Length == 0 || !tokenHasBase)
        {
            return;
        }

        if (builder.Length > 0)
        {
            _ = builder.Append(' ');
        }

        if (ReservedTokens.Contains(token))
        {
            _ = builder.Append('"');

            _ = builder.Append(token);

            _ = builder.Append('"');

            return;
        }

        _ = builder.Append(token);
    }
}
