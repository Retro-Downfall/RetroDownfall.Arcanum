using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The one place an erasure fingerprint is lifted: the operator's explicit release, and an operator
/// write that makes an erased identity live again.
/// </summary>
/// <remarks>
/// <para>Every fingerprint a release or a re-creation deletes is deleted here, through
/// <see cref="MemoryErasureEvidence.DeleteFingerprintAsync"/>, and an architecture test holds this file
/// as that member's only caller. The evidence SQL itself stays in the evidence store.</para>
///
/// <para>Nothing here opens, begins, commits or rolls back anything, and nothing reads the OS credential
/// store: every member runs on its caller's connection and transaction with whatever key the caller
/// already holds, so a re-creation inside an operator's write transaction never reaches the keychain.
/// Nothing here logs.</para>
///
/// <para>The tri-state answer is what an operator write reports. <see langword="true"/>: the identity's
/// fingerprint was (or would be) deleted. <see langword="false"/>: the store holds nothing this write
/// could release. <see langword="null"/>: the store holds fingerprints this write cannot check, because
/// it has no key, its identity cannot be fingerprinted, or the store holds rows another key recorded.</para>
/// </remarks>
internal static class MemoryErasureFingerprintRelease
{
    /// <summary>Deletes the fingerprint of each distinct candidate identity under <paramref name="key"/>.</summary>
    /// <returns>How many fingerprints were deleted.</returns>
    internal static async Task<int> DeleteCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryErasureKey key,
        IReadOnlyList<MemoryErasureIdentity> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(key);

        ArgumentNullException.ThrowIfNull(candidates);

        int deleted = 0;

        foreach (MemoryErasureIdentity candidate in candidates.Distinct())
        {
            deleted += await MemoryErasureEvidence
                .DeleteFingerprintAsync(connection, transaction, key.Fingerprint(candidate), cancellationToken)
                .ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>
    /// What <see cref="ReleaseForOperatorWriteAsync"/> would answer, read without deleting anything: the
    /// preflight's disclosure.
    /// </summary>
    internal static async Task<bool?> WouldReleaseAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryReviewStore store,
        MemoryErasureIdentity? identity,
        MemoryErasureKey? key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!await MemoryErasureEvidence.AnyAsync(connection, transaction, store, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (identity is not { } subject || key is null)
        {
            return null;
        }

        RequireStore(store, subject);

        if (await MemoryErasureEvidence
                .ContainsAsync(connection, transaction, key.Fingerprint(subject), cancellationToken)
                .ConfigureAwait(false))
        {
            return true;
        }

        return await ForeignAsync(connection, transaction, store, key, cancellationToken).ConfigureAwait(false)
            ? null
            : false;
    }

    /// <summary>
    /// Deletes the fingerprint of the identity an operator write just made live, inside that write's own
    /// transaction, and answers what it found (spec §5.6).
    /// </summary>
    /// <param name="identity">The identity the write re-created, or null when it cannot be fingerprinted.</param>
    /// <param name="key">The key the write captured from the latch before its transaction, or null.</param>
    internal static async Task<bool?> ReleaseForOperatorWriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryReviewStore store,
        MemoryErasureIdentity? identity,
        MemoryErasureKey? key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        if (!await MemoryErasureEvidence.AnyAsync(connection, transaction, store, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (identity is not { } subject || key is null)
        {
            return null;
        }

        RequireStore(store, subject);

        if (await MemoryErasureEvidence
                .DeleteFingerprintAsync(connection, transaction, key.Fingerprint(subject), cancellationToken)
                .ConfigureAwait(false) > 0)
        {
            return true;
        }

        return await ForeignAsync(connection, transaction, store, key, cancellationToken).ConfigureAwait(false)
            ? null
            : false;
    }

    /// <summary>Whether the store holds a fingerprint another key recorded, which this key can never match.</summary>
    private static Task<bool> ForeignAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryReviewStore store,
        MemoryErasureKey key,
        CancellationToken cancellationToken) =>
        MemoryErasureEvidence.AnyForeignAsync(connection, transaction, store, key.KeyId.ToArray(), cancellationToken);

    private static void RequireStore(MemoryReviewStore store, MemoryErasureIdentity identity)
    {
        if (identity.Store != store)
        {
            throw new ArgumentException("A fingerprint is released only from the store its identity names.", nameof(identity));
        }
    }
}
