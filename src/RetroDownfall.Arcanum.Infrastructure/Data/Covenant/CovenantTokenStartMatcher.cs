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
/// <para>Token characters follow <c>unicode61</c>, whose default categories are <c>L* N* Co</c>: letters,
/// numbers and private-use characters, plus the <c>._-</c> characters the index declares as token
/// characters. Every combining mark category (<c>Mn</c>, <c>Mc</c>, <c>Me</c>) separates tokens, so the
/// vowel signs and viramas of Indic scripts split a word the way the index splits it. The one exception
/// is the block of Latin combining diacritics (U+0300 to U+0331, the set the tokenizer's diacritic
/// removal covers): those continue the token they ride on, but never begin one, so a diacritic that
/// follows a separator does not make the word after it a mid-word position.</para>
///
/// <para>A term that itself opens with a separator has no token boundary to honour and is matched as
/// written, which is what the substring match it refines already did.</para>
///
/// <para>This is deliberately not a second tokenizer. Folding is ordinal and case-insensitive, with
/// no diacritic removal and no phrase splitting of a term that contains a separator, and characters are
/// classified by .NET's Unicode tables where the index uses SQLite's. Those remain differences of the
/// full-text index, which the API contract names.</para>
/// </remarks>
internal static class CovenantTokenStartMatcher
{
    /// <summary>The SQL function name the Covenant connection registers this rule under.</summary>
    internal const string FunctionName = "arcanum_token_start_contains";

    /// <summary>The kept diacritics among U+0300 to U+031F, one bit per scalar, as the tokenizer's table has them.</summary>
    private const uint KeptDiacriticsLow = 0x08029FDF;

    /// <summary>The kept diacritics among U+0320 to U+0331, one bit per scalar, as the tokenizer's table has them.</summary>
    private const uint KeptDiacriticsHigh = 0x000361F8;

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

    /// <summary>Whether this scalar can begin a token under the index's tokenizer settings.</summary>
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
            or UnicodeCategory.PrivateUse;

    /// <summary>
    /// Whether this scalar is one of the combining diacritics the index's tokenizer keeps inside a token:
    /// the same 50-character window (U+0300 to U+0331) and bit set its diacritic removal uses.
    /// </summary>
    internal static bool IsKeptDiacritic(Rune rune)
    {
        int offset = rune.Value - 0x0300;

        if (offset < 0 || offset > 0x31)
        {
            return false;
        }

        return offset < 32
            ? (KeptDiacriticsLow & (1u << offset)) != 0
            : (KeptDiacriticsHigh & (1u << (offset - 32))) != 0;
    }

    private static bool OpensWithTokenCharacter(string term) =>
        Rune.DecodeFromUtf16(term, out Rune first, out _) == System.Buffers.OperationStatus.Done
        && IsTokenCharacter(first);

    /// <summary>
    /// Whether the text that ends immediately before <paramref name="index"/> belongs to a token that
    /// reaches the index: the scalar there is a token character, or it is a run of kept diacritics that
    /// rides on one.
    /// </summary>
    private static bool EndsWithTokenCharacter(string value, int index)
    {
        int end = index;

        while (end > 0)
        {
            if (!TryReadScalarBefore(value, end, out Rune rune, out int width))
            {
                return false;
            }

            if (IsTokenCharacter(rune))
            {
                return true;
            }

            if (!IsKeptDiacritic(rune))
            {
                return false;
            }

            end -= width;
        }

        // Only diacritics lie in front, and a diacritic does not begin a token.
        return false;
    }

    /// <summary>Reads the scalar that ends at <paramref name="end"/>; a lone surrogate is not one.</summary>
    private static bool TryReadScalarBefore(string value, int end, out Rune rune, out int width)
    {
        char last = value[end - 1];

        if (char.IsLowSurrogate(last))
        {
            if (end >= 2 && char.IsHighSurrogate(value[end - 2]))
            {
                rune = new Rune(value[end - 2], last);

                width = 2;

                return true;
            }

            rune = default;

            width = 0;

            return false;
        }

        width = 1;

        return Rune.TryCreate(last, out rune);
    }
}
