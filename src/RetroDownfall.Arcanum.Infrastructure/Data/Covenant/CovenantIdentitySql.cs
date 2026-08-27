namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one shape every Covenant statement compares a stored identity in, and the binding that goes
/// with it.
/// </summary>
/// <remarks>
/// The tables Covenant reads and deletes from do not agree on how an identity is spelled. Saga writes
/// a lowercase 36-character form, the Lexicon and the idempotency claim store write 32 lowercase hex
/// characters with no dashes at all, and <c>"Entries"</c>, <c>"Sessions"</c>, and
/// <c>entry_embeddings</c> hold an uppercase spelling from the object-relational writer beside a
/// lowercase one from protected transfer and backup import in the same column. No single bound
/// literal matches every row of those three, so binding "the right form per target" would be
/// incorrect rather than merely verbose. The column is normalised instead, and the parameter is
/// bound already normalised.
///
/// <para><b>The cost, chosen rather than overlooked.</b> <c>lower(replace(col, '-', '')) = @id</c>
/// cannot use a BINARY-collated index, so every statement generated here degrades from an index seek
/// to a table scan. Each call site states what that costs it specifically. The index-preserving
/// alternative — <c>WHERE col IN ($upper, $lower, $n)</c> — is faster and silently wrong the first
/// time a writer spells an identity a fourth way, and this shape is not.</para>
///
/// <para>Named for the comparison rather than for the purge that first needed it, because a delete is
/// no longer the only statement that has to find a row whose spelling it did not choose: the
/// plaintext-export taint guard counts through this shape too.</para>
/// </remarks>
internal static class CovenantIdentitySql
{

    /// <summary>The keyed predicate for one column, against an already-normalised parameter.</summary>
    internal static string Keyed(string column, string parameter) =>
        $"lower(replace({column}, '-', '')) = {parameter}";

    /// <summary>The normalised spelling of a typed identity.</summary>
    /// <remarks>
    /// <c>N</c> is already lowercase and dash-free, which is exactly what
    /// <see cref="Keyed"/> reduces every stored spelling to.
    /// </remarks>
    internal static string Key(Guid value) => value.ToString("N");

    /// <summary>The normalised spelling of an identity read back out of a database.</summary>
    /// <remarks>
    /// Deliberately not <c>Guid.Parse(value).ToString("N")</c>. A staged archive is somebody else's
    /// database and may hold an identity this build cannot parse; normalising the text keeps such a
    /// row one that matches nothing, rather than an exception that fails a whole protected-state
    /// purge.
    /// </remarks>
    internal static string Key(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

}
