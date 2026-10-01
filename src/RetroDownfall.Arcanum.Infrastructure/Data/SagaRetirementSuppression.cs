using System.Data.Common;

using RetroDownfall.Arcanum.Core.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The pair of digests a Saga retirement suppression can be recorded under, and the one count and one
/// delete an erase makes of that pair.
/// </summary>
/// <remarks>
/// <para>The insert chokepoint and the reinstate release compute the pair here, and an erase counts
/// and deletes it here, so every reader of a retirement asks the same two digests.</para>
///
/// <para>The count and the delete read the installation's suppression key and never create it: a key
/// that does not exist means nothing was ever retired, so there is no pair to find and both answer
/// zero. Neither logs, and neither names content anywhere but in the digest it hashes.</para>
/// </remarks>
internal static class SagaRetirementSuppression
{
    /// <summary>
    /// The two digests a suppression can be recorded under: the one this installation writes now, and
    /// the one it wrote before the Campaign spelling was settled.
    /// </summary>
    /// <remarks>
    /// <b>The second is a compatibility branch that cannot be dropped.</b> The Campaign identity is part
    /// of the preimage, because a rejection made inside one Campaign is not an opinion about another.
    /// Before <c>session_campaign_bindings.CampaignId</c> was settled, the identity a retirement hashed
    /// was whichever spelling that Session's binding happened to carry, and for every Session created
    /// through the turn-begin path that was the minority form. A digest cannot be recomputed after the
    /// fact - retirement deletes the content that is its preimage - so an installation's existing
    /// suppression rows are the only copy of it.
    ///
    /// <para><b>Both halves of the lifecycle have to ask the same pair, and one of them did not.</b> The
    /// write path checks a suppression before adding a memory; the release path deletes one when an
    /// operator reinstates. Shipping the pair to the first and a single digest to the second made a
    /// memory retired before the upgrade impossible to un-retire: the delete matched nothing, the
    /// suppression stayed, and the reinstated memory was refused on the next extraction with nothing
    /// reporting why. Returning both from one place is what stops the two paths from disagreeing
    /// again.</para>
    ///
    /// <para><b>Sharing the function was not enough on its own, and the first attempt at this shared
    /// only that.</b> The pair is derived from whatever the caller hands in, and the two callers do not
    /// read the Campaign identity from the same place: the write path takes it from the classifier,
    /// which canonicalizes, while the release reads it out of the memory row, which the version-5 sweep
    /// may not have reached. Handed the minority spelling, this returned one digest twice - so a release
    /// asked only for the spelling the row happened to hold, removed nothing when the retirement had
    /// been recorded on the other half, and still reported success. Canonicalizing here rather than
    /// trusting the callers to agree is what makes the pair a property of the function.</para>
    ///
    /// <para>A Global or unresolved scope carries no Campaign, so both renderings are
    /// <see langword="null"/> and the two digests are the same value. Asking for it twice is
    /// harmless.</para>
    ///
    /// <para><c>RetireAsync</c> deliberately does not come through here: it writes one digest, over the
    /// spelling the memory row holds. The pair above covers it, because every spelling either binding
    /// writer has ever produced is the canonical form or its lowercase image. A parseable identity in
    /// any other casing would not be covered - but no writer can produce one, version 5's guard refuses
    /// one, and the sweep repairs one, since a mixed-case value is canonically shaped and
    /// <c>upper()</c> settles it.</para>
    /// </remarks>
    internal static (byte[] Settled, byte[] Legacy) Digests(
        byte[] key,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        string content)
    {
        string? settled = SagaMemoryScopeClassifier.CanonicalCampaignIdentity(campaignId);

        return (SagaSuppressionDigest.Compute(key, scopeKind, settled, content),
            SagaSuppressionDigest.Compute(
                key,
                scopeKind,
                settled?.ToLowerInvariant(),
                content));
    }

    /// <summary>How many suppression rows record this content in this scope, under either digest.</summary>
    /// <returns>The rows found, or zero when no suppression key exists.</returns>
    internal static async Task<int> CountPairAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        string content,
        CancellationToken cancellationToken)
    {
        await using DbCommand? command = await PairCommandAsync(
            connection,
            transaction,
            "SELECT count(*) FROM saga_retirement_suppressions WHERE SuppressionDigest IN (@settled, @legacy)",
            scopeKind,
            campaignId,
            content,
            cancellationToken).ConfigureAwait(false);

        if (command is null)
        {
            return 0;
        }

        // A count(*) always answers with an integer; anything else is refused rather than read as none.
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rows
            ? checked((int)rows)
            : throw new InvalidDataException("The retirement suppression count did not return an integer.");
    }

    /// <summary>Removes the suppression rows that record this content in this scope, under either digest.</summary>
    /// <returns>The rows removed, or zero when no suppression key exists.</returns>
    internal static async Task<int> DeletePairAsync(
        DbConnection connection,
        DbTransaction? transaction,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        string content,
        CancellationToken cancellationToken)
    {
        await using DbCommand? command = await PairCommandAsync(
            connection,
            transaction,
            "DELETE FROM saga_retirement_suppressions WHERE SuppressionDigest IN (@settled, @legacy)",
            scopeKind,
            campaignId,
            content,
            cancellationToken).ConfigureAwait(false);

        return command is null
            ? 0
            : await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The statement bound to the pair, or null when no suppression key exists to compute it.</summary>
    private static async Task<DbCommand?> PairCommandAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        SagaMemoryScopeKind scopeKind,
        string? campaignId,
        string content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(content);

        byte[]? key = await SagaSuppressionKeyStore
            .ReadAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        if (key is null)
        {
            return null;
        }

        (byte[] settled, byte[] legacy) = Digests(key, scopeKind, campaignId, content);

        DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        AddParameter(command, "@settled", settled);

        AddParameter(command, "@legacy", legacy);

        return command;
    }

    private static void AddParameter(DbCommand command, string name, byte[] value)
    {
        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = name;

        parameter.Value = value;

        _ = command.Parameters.Add(parameter);
    }
}
