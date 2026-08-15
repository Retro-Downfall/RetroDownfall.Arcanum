using System.Buffers;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public static class CovenantUnicodePolicyV1
{
    private const int ScalarMask = 0x1fffff;

    private const int MaximumRecursiveExpansion = 4;

    private const int HangulSyllableBase = 0xac00;

    private const int HangulLeadingBase = 0x1100;

    private const int HangulVowelBase = 0x1161;

    private const int HangulTrailingBase = 0x11a7;

    private const int HangulLeadingCount = 19;

    private const int HangulVowelCount = 21;

    private const int HangulTrailingCount = 28;

    private const int HangulSyllablesPerLeading = HangulVowelCount * HangulTrailingCount;

    private const int HangulSyllableCount = HangulLeadingCount * HangulSyllablesPerLeading;

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

        int capacity = checked(Math.Max(1, utf8ByteCount * MaximumRecursiveExpansion));
        int[] scalars = new int[capacity];

        try
        {
            int scalarCount = Decompose(value, scalars);

            ReorderByCanonicalCombiningClass(scalars.AsSpan(0, scalarCount));

            int composedCount = Compose(scalars, scalarCount);

            return CreateString(scalars.AsSpan(0, composedCount));
        }
        finally
        {
            scalars.AsSpan().Clear();
        }
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

    private static int Decompose(string value, int[] destination)
    {
        ReadOnlySpan<char> remaining = value.AsSpan();
        int count = 0;

        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int charsConsumed);

            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("Covenant text must contain valid Unicode scalar values.", nameof(value));
            }

            AppendCanonicalDecomposition(rune.Value, destination, ref count, 0);
            remaining = remaining[charsConsumed..];
        }

        return count;
    }

    private static void AppendCanonicalDecomposition(
        int scalar,
        int[] destination,
        ref int count,
        int depth)
    {
        if (depth > MaximumRecursiveExpansion)
        {
            throw new InvalidOperationException("Unicode 17 canonical decomposition exceeded its verified recursion bound.");
        }

        if (TryDecomposeHangul(scalar, out int leading, out int vowel, out int trailing))
        {
            AddScalar(destination, ref count, leading);
            AddScalar(destination, ref count, vowel);

            if (trailing != 0)
            {
                AddScalar(destination, ref count, trailing);
            }

            return;
        }

        if (!TryGetCanonicalDecomposition(scalar, out int first, out int second, out int length))
        {
            AddScalar(destination, ref count, scalar);
            return;
        }

        AppendCanonicalDecomposition(first, destination, ref count, depth + 1);

        if (length == 2)
        {
            AppendCanonicalDecomposition(second, destination, ref count, depth + 1);
        }
    }

    private static void AddScalar(int[] destination, ref int count, int scalar)
    {
        if ((uint)count >= (uint)destination.Length)
        {
            throw new ArgumentException("Unicode 17 canonical decomposition exceeded the bounded authored allocation.");
        }

        destination[count] = scalar;
        count = checked(count + 1);
    }

    private static bool TryDecomposeHangul(int scalar, out int leading, out int vowel, out int trailing)
    {
        int syllableIndex = scalar - HangulSyllableBase;

        if ((uint)syllableIndex >= HangulSyllableCount)
        {
            leading = 0;
            vowel = 0;
            trailing = 0;
            return false;
        }

        leading = HangulLeadingBase + (syllableIndex / HangulSyllablesPerLeading);
        vowel = HangulVowelBase + ((syllableIndex % HangulSyllablesPerLeading) / HangulTrailingCount);
        int trailingIndex = syllableIndex % HangulTrailingCount;
        trailing = trailingIndex == 0 ? 0 : HangulTrailingBase + trailingIndex;
        return true;
    }

    private static bool TryGetCanonicalDecomposition(
        int scalar,
        out int first,
        out int second,
        out int length)
    {
        ReadOnlySpan<ulong> records = CovenantUnicode17Tables.CanonicalDecompositions;
        int low = 0;
        int high = records.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ulong packed = records[middle];
            int candidate = checked((int)(packed >> 13));

            if (scalar < candidate)
            {
                high = middle - 1;
            }
            else if (scalar > candidate)
            {
                low = middle + 1;
            }
            else
            {
                int offset = checked((int)((packed >> 1) & 0x0fff));
                length = checked((int)(packed & 1) + 1);
                ReadOnlySpan<uint> decompositionScalars = CovenantUnicode17Tables.DecompositionScalars;
                first = checked((int)decompositionScalars[offset]);
                second = length == 2 ? checked((int)decompositionScalars[offset + 1]) : 0;
                return true;
            }
        }

        first = 0;
        second = 0;
        length = 0;
        return false;
    }

    private static void ReorderByCanonicalCombiningClass(Span<int> scalars)
    {
        for (int index = 1; index < scalars.Length; index++)
        {
            int scalar = scalars[index];
            int combiningClass = GetCanonicalCombiningClass(scalar);

            if (combiningClass == 0)
            {
                continue;
            }

            int insertionIndex = index;

            while (insertionIndex > 0)
            {
                int previousClass = GetCanonicalCombiningClass(scalars[insertionIndex - 1]);

                if (previousClass == 0 || previousClass <= combiningClass)
                {
                    break;
                }

                scalars[insertionIndex] = scalars[insertionIndex - 1];
                insertionIndex--;
            }

            scalars[insertionIndex] = scalar;
        }
    }

    private static int GetCanonicalCombiningClass(int scalar)
    {
        ReadOnlySpan<uint> records = CovenantUnicode17Tables.CombiningClasses;
        int low = 0;
        int high = records.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            uint packed = records[middle];
            int candidate = checked((int)(packed >> 8));

            if (scalar < candidate)
            {
                high = middle - 1;
            }
            else if (scalar > candidate)
            {
                low = middle + 1;
            }
            else
            {
                return checked((int)(packed & 0xff));
            }
        }

        return 0;
    }

    private static int Compose(int[] scalars, int scalarCount)
    {
        if (scalarCount == 0)
        {
            return 0;
        }

        int starterIndex = GetCanonicalCombiningClass(scalars[0]) == 0 ? 0 : -1;
        int starter = starterIndex == 0 ? scalars[0] : 0;
        int lastCombiningClass = GetCanonicalCombiningClass(scalars[0]);
        int writeIndex = 1;

        for (int readIndex = 1; readIndex < scalarCount; readIndex++)
        {
            int scalar = scalars[readIndex];
            int combiningClass = GetCanonicalCombiningClass(scalar);
            bool unblocked = lastCombiningClass == 0 || lastCombiningClass < combiningClass;

            if (starterIndex >= 0
                && unblocked
                && TryComposePair(starter, scalar, out int composite))
            {
                scalars[starterIndex] = composite;
                starter = composite;
                continue;
            }

            if (combiningClass == 0)
            {
                starterIndex = writeIndex;
                starter = scalar;
            }

            lastCombiningClass = combiningClass;
            scalars[writeIndex] = scalar;
            writeIndex = checked(writeIndex + 1);
        }

        return writeIndex;
    }

    private static bool TryComposePair(int first, int second, out int composite)
    {
        int leadingIndex = first - HangulLeadingBase;
        int vowelIndex = second - HangulVowelBase;

        if ((uint)leadingIndex < HangulLeadingCount && (uint)vowelIndex < HangulVowelCount)
        {
            composite = HangulSyllableBase
                + (leadingIndex * HangulSyllablesPerLeading)
                + (vowelIndex * HangulTrailingCount);
            return true;
        }

        int syllableIndex = first - HangulSyllableBase;
        int trailingIndex = second - HangulTrailingBase;

        if ((uint)syllableIndex < HangulSyllableCount
            && syllableIndex % HangulTrailingCount == 0
            && trailingIndex is > 0 and < HangulTrailingCount)
        {
            composite = first + trailingIndex;
            return true;
        }

        ulong pair = ((ulong)first << 21) | (uint)second;
        ReadOnlySpan<ulong> records = CovenantUnicode17Tables.Compositions;
        int low = 0;
        int high = records.Length - 1;

        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            ulong packed = records[middle];
            ulong candidate = packed >> 21;

            if (pair < candidate)
            {
                high = middle - 1;
            }
            else if (pair > candidate)
            {
                low = middle + 1;
            }
            else
            {
                composite = checked((int)(packed & ScalarMask));
                return true;
            }
        }

        composite = 0;
        return false;
    }

    private static string CreateString(ReadOnlySpan<int> scalars)
    {
        int charCount = 0;

        foreach (int scalar in scalars)
        {
            charCount = checked(charCount + (scalar <= 0xffff ? 1 : 2));
        }

        char[] characters = new char[charCount];
        int index = 0;

        try
        {
            foreach (int scalar in scalars)
            {
                if (scalar <= 0xffff)
                {
                    characters[index] = (char)scalar;
                    index++;
                    continue;
                }

                int surrogate = scalar - 0x10000;
                characters[index] = (char)(0xd800 + (surrogate >> 10));
                characters[index + 1] = (char)(0xdc00 + (surrogate & 0x03ff));
                index += 2;
            }

            return new string(characters);
        }
        finally
        {
            characters.AsSpan().Clear();
        }
    }
}
