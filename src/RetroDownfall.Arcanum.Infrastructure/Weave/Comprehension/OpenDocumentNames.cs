namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// The OpenDocument namespace URIs and the small attribute parsers the three OpenDocument extractors share.
/// Elements are always matched by local name plus namespace URI, never by prefix.
/// </summary>
internal static class OpenDocumentNames
{
    public const string Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    public const string Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    public const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    public const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";

    public const string Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    public const string Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";

    public const string Svg = "urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0";

    /// <summary>The largest repeat count honoured; larger values saturate here so sums of counts cannot overflow.</summary>
    public const long MaximumRepeat = 1L << 40;

    /// <summary>
    /// Parses a decimal count attribute. Missing, empty, signed, zero or non-numeric text yields 1 (the ODF default),
    /// and a value above <paramref name="maximum"/> saturates at it. No loop ever runs for a parsed count.
    /// </summary>
    public static long ParseCount(string? value, long maximum)
    {
        if (value is null)
        {
            return 1;
        }

        ReadOnlySpan<char> digits = value.AsSpan().Trim();

        if (digits.IsEmpty)
        {
            return 1;
        }

        long result = 0;

        foreach (char digit in digits)
        {
            if (digit is < '0' or > '9')
            {
                return 1;
            }

            result = (result * 10) + (digit - '0');

            if (result > maximum)
            {
                return maximum;
            }
        }

        return result == 0 ? 1 : result;
    }
}
