namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The comparison shape for an identity whose spelling this build does not control, and the binding
/// that goes with it.
/// </summary>
/// <remarks>
/// <b>Narrowed, and the narrowing is the point.</b> This was once the one shape every statement in
/// this namespace compared an identity in, because the tables Covenant reads did not agree on how an
/// identity was spelled and no single bound literal matched every row. That is settled: the Grimoire's
/// governed identity columns hold the canonical uppercase dashed form, refused at the write by a guard
/// trigger and verified on upgrade by a schema-version sweep, and every reader of one of them compares
/// exactly and seeks its index again. What is left here are the two cases a settled database does not
/// answer.
///
/// <para><b>Foreign data whose vintage this build does not control.</b> A backup archive and a staged
/// restore are snapshots of some other installation, taken at some other time. One taken before the
/// version-5 attachment sweep holds <c>SessionAttachments."SessionId"</c> in the minority spelling and
/// one taken after holds the canonical form, and a selective import has to read both. Binding either
/// spelling exactly fixes one vintage by silently breaking the other, and the failure has no sound:
/// the read returns no rows, so the import copies no attachment, throws nothing, and reports
/// completed. The rule, stated once here for the call sites that follow it: normalise when reading
/// foreign data whose vintage you do not control, compare exactly when reading your own.</para>
///
/// <para><b>Columns outside the canonicalisation family.</b> The artifact purge plans reach
/// <c>saga_memories</c>, the Lexicon, the idempotency claim store and the vector mirrors, whose
/// identity columns are deliberately not of this family — two of them render 32 dash-free characters
/// by design. A canonical literal matches none of those, so the statements that span them normalise
/// the column instead, and one shared shape is what keeps the live erasure kernel and the staged purge
/// from finding a row two different ways.</para>
///
/// <para><b>The cost, chosen rather than overlooked.</b> <c>lower(replace(col, '-', '')) = @id</c>
/// cannot use a BINARY-collated index, so every statement generated here degrades from an index seek
/// to a table scan. Each call site states what that costs it specifically. No remaining caller is on a
/// per-turn path — that was the reason the data was settled — and the index-preserving alternative,
/// <c>WHERE col IN ($upper, $lower, $n)</c>, is faster and silently wrong the first time a writer
/// spells an identity a fourth way.</para>
///
/// <para>Scoped to Covenant deliberately, rather than claimed as the installation's one spelling of
/// the predicate. The retention service writes the same comparison by hand in roughly thirty places,
/// including over tables Covenant owns; it is the precedent this shape follows rather than a caller
/// of it, and saying otherwise here would be a claim a reader could disprove with one grep.</para>
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
