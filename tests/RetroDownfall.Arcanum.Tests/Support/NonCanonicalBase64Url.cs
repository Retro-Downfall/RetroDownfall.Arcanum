namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// Builds the unpadded base64url spellings no encoder ever emits, which a lenient decoder still reads
/// and the framework's throwing decoder answers with <see cref="FormatException"/>.
/// </summary>
internal static class NonCanonicalBase64Url
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>
    /// The same value with the lowest of the unused trailing bits of its final character set. The length
    /// is unchanged and every character is in the alphabet, so only a canonical check can tell it from
    /// the real thing.
    /// </summary>
    /// <remarks>
    /// A value whose length is a multiple of four has no unused bits, so there is no such spelling of it
    /// and the call refuses rather than hand back a different, perfectly valid, value.
    /// </remarks>
    internal static string WithUnusedBitSet(string canonical)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonical);

        if (canonical.Length % 4 is 0 or 1)
        {
            throw new InvalidOperationException(
                "An encoded value of " + canonical.Length + " characters has no unused trailing bits.");
        }

        int index = Alphabet.IndexOf(canonical[^1], StringComparison.Ordinal);

        if (index < 0 || (index & 1) != 0)
        {
            throw new InvalidOperationException("The value does not end in a canonical final character.");
        }

        return canonical[..^1] + Alphabet[index | 1];
    }

    /// <summary>
    /// The value cut to the nearest length that is one more than a multiple of four. One character cannot
    /// carry a whole byte, so no decoder can read it, and the framework's throwing decoder raises
    /// <see cref="FormatException"/> for it.
    /// </summary>
    internal static string WithAnImpossibleLength(string encoded)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoded);

        return encoded[..(((encoded.Length - 1) / 4 * 4) + 1)];
    }
}
