using Microsoft.Data.Sqlite;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// The one shape every statement in this namespace compares a stored identity in, and the binding
/// that goes with it.
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

    /// <summary>
    /// The exact text <c>"Sessions"."Id"</c> holds for one Session, or <see langword="null"/> when no
    /// Session row carries that identity in any spelling.
    /// </summary>
    /// <remarks>
    /// For a child row that has to satisfy a foreign key rather than a predicate. Every projection
    /// Covenant hangs off a Session — <c>session_sensitivity_state</c>,
    /// <c>session_summary_artifacts</c>, <c>session_summary_state</c>, and their title equivalents —
    /// declares <c>REFERENCES "Sessions" ("Id")</c>, and SQLite resolves a reference by byte equality
    /// against the parent column under its own collation. There is no predicate to normalise there, so
    /// <see cref="Keyed"/> cannot help: a child written in the ledger's uppercase spelling is rejected
    /// outright for a Session the protected transfer store or the backup importer created, because
    /// those two write <c>ToString("D")</c> while the object-relational writer writes it uppercased.
    /// Normalising the comparison is not available to a foreign key; agreeing with the parent is.
    ///
    /// <para>Null rather than a manufactured spelling when nothing resolves, so a caller writing a
    /// projection for a Session that does not exist fails its foreign key exactly as it does today
    /// instead of having its identity quietly rewritten into some other shape.</para>
    ///
    /// <para><b>The cost.</b> One normalised scan of <c>"Sessions"</c> per artifact write, because the
    /// primary-key index cannot serve a normalised column. <c>"Sessions"</c> holds one row per
    /// conversation rather than one per message, and every caller is already inside a write
    /// transaction that is about to insert several rows, so this is not what dominates. Deliberately
    /// not cached: the spelling is a property of a row another writer owns, and a cached answer would
    /// outlive a Session that was deleted and recreated.</para>
    /// </remarks>
    internal static async Task<string?> ResolveStoredSessionIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        // ORDER BY, because this is the single point of truth for a foreign key and an arbitrary
        // answer there is worse than a wrong one. "Sessions"."Id" is a BINARY-collated TEXT primary
        // key, so two rows whose identities differ only in case are representable — this build writes
        // no such pair, but a database that already holds one must resolve the same spelling on every
        // call rather than whichever row the query plan reached first.
        command.CommandText = $"""
            SELECT "Id" FROM "Sessions" WHERE {Keyed("\"Id\"", "$sessionKey")} ORDER BY "Id" LIMIT 1;
            """;

        _ = command.Parameters.AddWithValue("$sessionKey", Key(sessionId));

        object? stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return stored as string;

    }

}
