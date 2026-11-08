using System.Globalization;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>Rejects rounded or underflowed decimal text at authority and persistence boundaries.</summary>
internal static class ExactDecimalText
{
    internal static bool TryParse(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out value)
        && TryNormalize(text, out var original)
        && TryNormalize(value.ToString(CultureInfo.InvariantCulture), out var represented)
        && original == represented;

    // Decimal.TryParse rounds excess fractional precision and can underflow to zero. Authoritative
    // TEXT must instead denote the exact represented value, including exponent-form legacy rows.
    private static bool TryNormalize(string text, out (bool Negative, string Digits, long Exponent) normalized)
    {
        normalized = default;

        bool negative = text.StartsWith('-');

        if (text.StartsWith('-') || text.StartsWith('+')) text = text[1..];

        int exponentAt = text.IndexOfAny(['e', 'E']);

        string mantissa = exponentAt < 0 ? text : text[..exponentAt];

        int point = mantissa.IndexOf('.');

        int fractionalDigits = point < 0 ? 0 : mantissa.Length - point - 1;

        string digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('0');

        if (digits.Length == 0)
        {
            normalized = (false, "0", 0);

            return true;
        }

        long exponent = 0;

        if (exponentAt >= 0 && !long.TryParse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent)) return false;

        string significant = digits.TrimEnd('0');

        try
        {
            normalized = (negative, significant, checked(exponent - fractionalDigits + digits.Length - significant.Length));

            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

}
