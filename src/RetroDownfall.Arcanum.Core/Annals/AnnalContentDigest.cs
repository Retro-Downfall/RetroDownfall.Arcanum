using System.Security.Cryptography;

using System.Text;

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
    /// entry would share a binding. A unit separator cannot occur in either field: both are collapsed
    /// and control-stripped long before they reach durable storage.
    /// </summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>The binding for one Saga memory's stored content.</summary>
    public static byte[] ForSagaMemory(string content)
    {

        ArgumentNullException.ThrowIfNull(content);

        return SHA256.HashData(Encoding.UTF8.GetBytes(content));

    }

    /// <summary>The binding for one Lexicon entity's type and fact set.</summary>
    /// <param name="factsText">
    /// The newline-joined projection <c>lexicon_entries.FactsText</c> stores, not the JSON. The
    /// projection is what changes when a fact is appended, and it is what the full-text index already
    /// agrees is the entry's content.
    /// </param>
    public static byte[] ForLexiconEntry(string type, string factsText)
    {

        ArgumentNullException.ThrowIfNull(type);

        ArgumentNullException.ThrowIfNull(factsText);

        return SHA256.HashData(Encoding.UTF8.GetBytes($"{type}{FieldSeparator}{factsText}"));

    }

}
