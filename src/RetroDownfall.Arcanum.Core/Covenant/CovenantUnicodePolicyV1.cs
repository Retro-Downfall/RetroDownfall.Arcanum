using System.Text;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

public static class CovenantUnicodePolicyV1
{
    private const int ScalarMask = 0x1fffff;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int CanonicalDecompositionCount => CovenantUnicode17Tables.CanonicalDecompositionCount;

    public static int NonzeroCombiningClassCount => CovenantUnicode17Tables.NonzeroCombiningClassCount;

    public static int FullCompositionExclusionCount => CovenantUnicode17Tables.FullCompositionExclusionCount;

    public static int CompositionPairCount => CovenantUnicode17Tables.CompositionPairCount;

    public static int FormatScalarCount => CovenantUnicode17Tables.FormatScalarCount;

    public static int FormatRangeCount => CovenantUnicode17Tables.FormatRangeCount;

    public static string NormalizeToNfc(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        int utf8ByteCount;

        try
        {
            utf8ByteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Covenant text must contain valid Unicode scalar values.", nameof(value), exception);
        }

        if (utf8ByteCount > CovenantLimits.MaxAuthoredContentBytes)
        {
            throw new ArgumentException(
                $"Covenant text cannot exceed {CovenantLimits.MaxAuthoredContentBytes} strict UTF-8 bytes.",
                nameof(value));
        }

        return UnicodeNfc.Normalize(value, utf8ByteCount);
    }

    public static bool IsFormatScalar(int scalar)
    {
        ReadOnlySpan<ulong> ranges = CovenantUnicode17Tables.FormatRanges;
        int low = 0;
        int high = ranges.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ulong packed = ranges[middle];
            int start = checked((int)(packed >> 21));
            int end = checked((int)(packed & ScalarMask));

            if (scalar < start)
            {
                high = middle - 1;
            }
            else if (scalar > end)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsWhitespaceScalar(int scalar) =>
        scalar is 0x0009 or 0x000a or 0x000d or 0x0020 or 0x00a0 or 0x1680
            or >= 0x2000 and <= 0x200a
            or >= 0x2028 and <= 0x2029
            or 0x202f or 0x205f or 0x3000;
}
