using System.Globalization;

using System.Security.Cryptography;

using System.Text;

namespace RetroDownfall.Arcanum.Core.Weave;

/// <summary>
/// The 32-byte keyed binding between a retirement and the content-and-scope it refuses to see again.
/// </summary>
/// <remarks>
/// Keyed rather than hashed, for two narrow reasons and no broader claim. The Annals already stores a
/// bare SHA-256 of the same bytes, so an unkeyed digest would be that identical value and the two
/// tables would join into one confirmation oracle rather than none. And destroying the single key row
/// makes every surviving digest permanently useless for confirming a guess about content that has
/// since been erased, which one row cannot do for an unkeyed hash. The Grimoire is encrypted at rest,
/// so this is not what keeps the content from someone who can read the file.
///
/// <para>The scope is part of the preimage because a rejection made inside one Campaign is not an
/// opinion about another. That is the same reasoning Campaign-scoped retrieval applies to what a turn
/// may recall.</para>
/// </remarks>
public static class SagaSuppressionDigest
{

    /// <summary>
    /// Separates the preimage's fields. A unit separator cannot occur in a scope code or a Campaign
    /// identity, and the content field is last, so no value can move a field boundary.
    /// </summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>Domain separation from every other keyed value this installation derives.</summary>
    private const string Domain = "arcanum/saga/retirement-suppression/v1";

    /// <summary>The binding for one retired memory's content, under its own ownership.</summary>
    public static byte[] Compute(
        ReadOnlySpan<byte> key,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        string content)
    {

        ArgumentNullException.ThrowIfNull(content);

        string preimage = string.Create(
            CultureInfo.InvariantCulture,
            $"{Domain}{FieldSeparator}{(int)scopeKind}{FieldSeparator}{campaignId ?? string.Empty}{FieldSeparator}{content}");

        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(preimage));

    }

}
