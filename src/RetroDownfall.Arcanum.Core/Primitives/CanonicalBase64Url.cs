using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;

namespace RetroDownfall.Arcanum.Core.Primitives;

/// <summary>
/// The one decoder for canonical unpadded base64url: it accepts exactly the spelling
/// <see cref="Base64Url.EncodeToString(ReadOnlySpan{byte})"/> writes, and answers every other value with
/// <c>false</c>. It never throws for a value it was handed.
/// </summary>
/// <remarks>
/// <para>Every key, nonce, tag, ciphertext and token Arcanum reads back arrives from somewhere that can
/// be corrupted or tampered with, so a value that does not decode is an ordinary outcome and has to be a
/// typed refusal. The framework's <c>Base64Url.TryDecodeFromChars</c> is not that: it returns false only
/// for a destination that is too small and raises <see cref="FormatException"/> for everything else
/// malformed, including a length no decoder can read and a final character that sets bits no encoder
/// emits. A copy of the decode that called it let the exception out of a credential-store read.</para>
///
/// <para>A value is accepted only when every one of these holds, and a refusal at any step zeroes whatever
/// was decoded so far:</para>
/// <list type="bullet">
/// <item><description>every character is in the base64url alphabet, so no padding, no standard-alphabet
/// <c>+</c> or <c>/</c>, no whitespace and no non-ASCII look-alike;</description></item>
/// <item><description>the status overload reports <see cref="OperationStatus.Done"/> and consumed every
/// character;</description></item>
/// <item><description>the decoded length is the length the caller asked for, for the two methods that
/// return an array;</description></item>
/// <item><description>re-encoding the decoded bytes gives back the very same characters, which leaves a
/// value exactly one spelling and so exactly one name.</description></item>
/// </list>
///
/// <para>The checks overlap on purpose. The framework decoder tolerates padding and whitespace, which
/// the alphabet check and the re-encode comparison each refuse, and it is the status, the consumed
/// count and the re-encode comparison that each refuse a final character setting unused bits. Any one
/// layer is enough for the inputs the framework is known to mishandle. All of them stay because which
/// of those behaviours holds is a property of a framework release, and the re-encode comparison is the
/// one check that does not depend on it.</para>
/// </remarks>
public static class CanonicalBase64Url
{
    /// <summary>
    /// Decodes <paramref name="encoded"/> into the front of <paramref name="destination"/>.
    /// </summary>
    /// <param name="encoded">The value to decode.</param>
    /// <param name="destination">
    /// Where the bytes go. It is left alone beyond <paramref name="written"/> on success, and zero from
    /// end to end on a refusal.
    /// </param>
    /// <param name="written">The number of bytes decoded, or zero on a refusal.</param>
    /// <returns>Whether <paramref name="encoded"/> is the canonical spelling of what it decodes to.</returns>
    public static bool TryDecode(string? encoded, Span<byte> destination, out int written)
    {
        written = 0;

        if (string.IsNullOrEmpty(encoded) || !IsAlphabet(encoded))
        {
            CryptographicOperations.ZeroMemory(destination);

            return false;
        }

        if (Base64Url.DecodeFromChars(encoded, destination, out int consumed, out int decoded)
                is not OperationStatus.Done
            || consumed != encoded.Length
            || !string.Equals(
                Base64Url.EncodeToString(destination[..decoded]),
                encoded,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(destination);

            return false;
        }

        written = decoded;

        return true;
    }

    /// <summary>
    /// Decodes <paramref name="encoded"/> when it is the canonical spelling of exactly
    /// <paramref name="expectedBytes"/> bytes.
    /// </summary>
    /// <param name="encoded">The value to decode.</param>
    /// <param name="expectedBytes">The exact number of bytes the value must decode to.</param>
    /// <param name="decoded">The bytes, in an array of their own, or an empty array on a refusal.</param>
    /// <returns>Whether <paramref name="encoded"/> is the canonical spelling of that many bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedBytes"/> is not positive.</exception>
    public static bool TryDecodeExact(string? encoded, int expectedBytes, out byte[] decoded) =>
        TryDecodeBounded(encoded, expectedBytes, expectedBytes, out decoded);

    /// <summary>
    /// Decodes <paramref name="encoded"/> when it is the canonical spelling of between
    /// <paramref name="minimumBytes"/> and <paramref name="maximumBytes"/> bytes.
    /// </summary>
    /// <remarks>
    /// The character count is held to the largest legal encoding before anything is allocated or
    /// scanned, and the buffer is sized from the value rather than from the ceiling, so a hostile value
    /// costs no more than its own length.
    /// </remarks>
    /// <param name="encoded">The value to decode.</param>
    /// <param name="minimumBytes">The fewest bytes the value may decode to.</param>
    /// <param name="maximumBytes">The most bytes the value may decode to.</param>
    /// <param name="decoded">The bytes, in an array of their own, or an empty array on a refusal.</param>
    /// <returns>Whether <paramref name="encoded"/> is the canonical spelling of a length in bounds.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimumBytes"/> is not positive, or <paramref name="maximumBytes"/> is smaller.
    /// </exception>
    public static bool TryDecodeBounded(string? encoded, int minimumBytes, int maximumBytes, out byte[] decoded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumBytes, 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, minimumBytes);

        decoded = [];

        if (string.IsNullOrEmpty(encoded) || encoded.Length > Base64Url.GetEncodedLength(maximumBytes))
        {
            return false;
        }

        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];

        try
        {
            if (!TryDecode(encoded, buffer, out int written) || written < minimumBytes)
            {
                return false;
            }

            decoded = buffer[..written];

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool IsAlphabet(string encoded)
    {
        foreach (char value in encoded)
        {
            bool allowed = value is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-'
                or '_';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
