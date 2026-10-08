using System.Security.Cryptography;

using System.Text;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Annals;

/// <summary>
/// The 32-byte binding between a claim version and the exact bytes it was written about.
/// </summary>
/// <remarks>
/// A binding rather than a copy. It proves which content a version describes without being able to
/// reconstruct it, which is what lets an operator erase a memory without leaving a record that still
/// carries what they asked to remove.
/// </remarks>
public static class AnnalContentDigest
{
    /// <summary>
    /// Separates a Lexicon entry's type from its fact set. Without it a type ending in text that the
    /// fact set begins with would hash identically to a different pair, and two distinct states of one
    /// entry would share a binding.
    /// </summary>
    /// <remarks>
    /// The separator is not exclusive. The Lexicon normalizer trims and bounds both fields but does not
    /// strip control characters, so a type or a fact may itself contain U+001F and the pairs
    /// <c>("a\u001Fb", "c")</c> and <c>("a", "b\u001Fc")</c> share one format-1 binding. That is a
    /// known boundary of format 1, pinned by <c>AnnalContentDigestTests</c>; changing the framing would
    /// change every stored binding, so the bytes are left exactly as they are.
    /// </remarks>
    private const char FieldSeparator = '\u001F';

    /// <summary>The binding for one Saga memory's stored content.</summary>
    /// <exception cref="EncoderFallbackException">
    /// <paramref name="content"/> holds an unpaired surrogate. Hashing the U+FFFD a lenient encoder would
    /// substitute would bind a claim to bytes the text never had, so the digest refuses instead.
    /// </exception>
    public static byte[] ForSagaMemory(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        return SHA256.HashData(StrictUtf8.Encoding.GetBytes(content));
    }

    /// <summary>The binding for one Lexicon entity's type and fact set.</summary>
    /// <exception cref="EncoderFallbackException">
    /// <paramref name="type"/> or <paramref name="factsText"/> holds an unpaired surrogate (see
    /// <see cref="ForSagaMemory"/>).
    /// </exception>
    /// <param name="factsText">
    /// The newline-joined projection <c>lexicon_entries.FactsText</c> stores, not the JSON. The
    /// projection is what changes when a fact is appended, and it is what the full-text index already
    /// agrees is the entry's content.
    /// </param>
    public static byte[] ForLexiconEntry(string type, string factsText)
    {
        ArgumentNullException.ThrowIfNull(type);

        ArgumentNullException.ThrowIfNull(factsText);

        return SHA256.HashData(StrictUtf8.Encoding.GetBytes($"{type}{FieldSeparator}{factsText}"));
    }
}
