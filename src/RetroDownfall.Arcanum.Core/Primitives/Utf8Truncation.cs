using System.Text;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// Shared UTF-8-safe string slicing helpers. .NET strings are UTF-16; a naive char-count or byte-count
/// cutoff can land between the two <see cref="char"/> halves of a surrogate pair (an astral-plane
/// character such as most emoji), leaving one fragment ending in a lone high surrogate and the next
/// beginning with its orphaned low surrogate — both invalid Unicode once re-encoded. Every truncation
/// boundary computed here is nudged back one <see cref="char"/> in that case so a surrogate pair is
/// always kept whole (or dropped whole), never split.
/// </summary>
public static class Utf8Truncation
{

    /// <summary>
    /// Returns the largest character count from the start of <paramref name="text"/> whose UTF-8
    /// encoding does not exceed <paramref name="maxUtf8Bytes"/>, without splitting a surrogate pair at
    /// the boundary. Used to hard-cap outbound payload size (bytes), as opposed to
    /// <see cref="SafeCharSliceLength(ReadOnlySpan{char}, int)"/> which caps by character count.
    /// </summary>
    public static int ChooseSafeCharCount(ReadOnlySpan<char> text, long maxUtf8Bytes)
    {

        long running = 0L;

        int i = 0;

        while (i < text.Length)
        {

            // Measure a complete surrogate pair as one 2-char unit rather than char-by-char: UTF8
            // byte-counting a lone surrogate half in isolation encodes it as the 3-byte replacement
            // character (U+FFFD), which both misrepresents its real 4-byte paired encoding and can
            // never land the cursor mid-pair in the first place.
            int unitLength = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;

            int unitByteSize = Encoding.UTF8.GetByteCount(text.Slice(i, unitLength));

            if (running + unitByteSize > maxUtf8Bytes)
            {
                break;

            }

            running += unitByteSize;

            i += unitLength;

        }

        return i;

    }

    /// <summary>
    /// Hard-truncates <paramref name="text"/> to at most <paramref name="maxUtf8Bytes"/> UTF-8 bytes,
    /// slicing on a surrogate-safe char boundary. Returns <paramref name="text"/> unchanged when it is
    /// already within budget, and <see cref="string.Empty"/> for a non-positive budget.
    /// </summary>
    public static string TruncateToUtf8ByteBudget(string text, long maxUtf8Bytes)
    {

        if (string.IsNullOrEmpty(text) || maxUtf8Bytes <= 0L)
        {
            return string.Empty;

        }

        long byteCount = Encoding.UTF8.GetByteCount(text);

        if (byteCount <= maxUtf8Bytes)
        {
            return text;

        }

        int safeCharCount = ChooseSafeCharCount(text, maxUtf8Bytes);

        return safeCharCount <= 0 ? string.Empty : text[..safeCharCount];

    }

    /// <summary>
    /// Returns the largest character count from the start of <paramref name="text"/>, up to
    /// <paramref name="maxChars"/>, that does not split a surrogate pair at the boundary. Used to
    /// hard-cap outbound text by character count (for example against
    /// <c>Arcanum:Embeddings:ChunkSizeChars</c>) rather than by UTF-8 byte budget.
    /// </summary>
    public static int SafeCharSliceLength(ReadOnlySpan<char> text, int maxChars)
    {

        if (maxChars <= 0)
        {
            return 0;

        }

        if (text.Length <= maxChars)
        {
            return text.Length;

        }

        int length = maxChars;

        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;

        }

        return length;

    }

}
