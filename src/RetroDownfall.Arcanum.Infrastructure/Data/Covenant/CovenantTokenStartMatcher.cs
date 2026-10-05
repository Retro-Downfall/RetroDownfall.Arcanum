using System.Globalization;
using System.Text;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The token-prefix rule the canonical search fallback applies, so it answers a query the way the
/// full-text index does.
/// </summary>
/// <remarks>
/// The full-text index tokenizes with <c>unicode61</c> and treats every query term as a prefix of a
/// token, so <c>spor</c> finds <c>sport</c> and <c>ort</c> does not. A <c>LIKE '%ort%'</c> finds both,
/// which made a hit appear while the accelerator was behind and vanish once it caught up. This rule
/// accepts an occurrence of a term only where a token can begin: at the start of the text, or after a
/// character that is not part of a token.
///
/// <para>Token characters follow <c>unicode61</c>: letters, numbers and private-use characters, the
/// combining marks that ride on them, and the <c>._-</c> characters the index declares as token
/// characters. Everything else separates tokens. A term that itself opens with a separator has no
/// token boundary to honour and is matched as written, which is what the substring match it refines
/// already did.</para>
///
/// <para>This is deliberately not a second tokenizer. Folding is ordinal and case-insensitive, with
/// no diacritic removal and no phrase splitting of a term that contains a separator: those remain
/// differences of the full-text index, which the API contract names.</para>
/// </remarks>
internal static class CovenantTokenStartMatcher
{
    /// <summary>The SQL function name the Covenant connection registers this rule under.</summary>
    internal const string FunctionName = "arcanum_token_start_contains";

    /// <summary>Whether <paramref name="term"/> occurs in <paramref name="value"/> where a token can begin.</summary>
    internal static bool Contains(string? value, string? term)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(term))
        {
            return false;
        }

        bool honoursBoundary = OpensWithTokenCharacter(term);

        int from = 0;

        while (from <= value.Length - term.Length)
        {
            int index = value.IndexOf(term, from, StringComparison.OrdinalIgnoreCase);

            if (index < 0)
            {
                return false;
            }

            if (!honoursBoundary || index == 0 || !EndsWithTokenCharacter(value, index))
            {
                return true;
            }

            from = index + 1;
        }

        return false;
    }

    /// <summary>Whether this scalar belongs to a token under the index's tokenizer settings.</summary>
    internal static bool IsTokenCharacter(Rune rune) =>
        rune.Value is '.' or '_' or '-'
        || Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    private static bool OpensWithTokenCharacter(string term) =>
        Rune.DecodeFromUtf16(term, out Rune first, out _) == System.Buffers.OperationStatus.Done
        && IsTokenCharacter(first);

    /// <summary>Whether the scalar that ends immediately before <paramref name="index"/> is a token character.</summary>
    private static bool EndsWithTokenCharacter(string value, int index)
    {
        char previous = value[index - 1];

        if (char.IsLowSurrogate(previous))
        {
            return index >= 2
                && char.IsHighSurrogate(value[index - 2])
                && IsTokenCharacter(new Rune(value[index - 2], previous));
        }

        // A lone high surrogate is not a scalar at all, so it separates like any other non-token.
        return Rune.TryCreate(previous, out Rune rune) && IsTokenCharacter(rune);
    }
}
