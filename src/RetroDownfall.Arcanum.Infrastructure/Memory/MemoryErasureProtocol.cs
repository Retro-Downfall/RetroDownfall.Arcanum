using System.Security.Cryptography;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// The steps every store's erase shares: schema readiness, key access, receipt-first replay, and the
/// post-commit scrub that finishes a result.
/// </summary>
/// <remarks>
/// <para><b>Key access runs before any transaction.</b> Opening the key can read the OS credential
/// store, which never happens inside a SQLite transaction (spec §5.1), so both openings refuse a
/// connection that is inside one, whether the transaction is an object or a raw <c>BEGIN</c>.
/// Prepare opens the key through the creator, which creates it only when no evidence row exists in
/// any store. Apply re-probes through the provider, because an erase is operator-initiated: a failure
/// an automatic probe latched is asked again rather than repeated. Either way a present key that
/// cannot verify the store's evidence is lost.</para>
///
/// <para><b>Replay is by receipt.</b> A receipt whose store and request digest match answers the
/// apply; one that differs is an idempotency conflict. The comparison is constant-time.</para>
///
/// <para><b>The result is the receipt's.</b> After the erase commits, a receipt still pending only
/// for the write-ahead log is scrubbed once and, when the log truncates, moved to Verified. The result
/// is then built from the receipt alone, so a replay and the original apply report the same erase.
/// Every caller finishes with <see cref="CancellationToken.None"/> after its commit, and nothing here
/// lets a storage fault escape once the erase has committed.</para>
///
/// <para>Nothing here logs.</para>
/// </remarks>
internal static class MemoryErasureProtocol
{
    private const int ScrubPending = 1;

    private const int ScrubVerified = 2;

    private const int WalCheckpointPendingBit = 1;

    internal static Error UnavailableError { get; } = new(
        ErrorCodes.MemoryErasure.Unavailable,
        "Selective erasure is unavailable until the Grimoire schema reaches the version that records erasures; retry shortly.");

    internal static Error KeyLostError { get; } = new(
        ErrorCodes.MemoryErasure.KeyLost,
        "Erasure fingerprints exist that this installation's erasure key cannot verify, so no erasure can be recorded. Run 'arcanum memory erasure status'.");

    internal static Error KeyUnavailableError { get; } = new(
        ErrorCodes.MemoryErasure.KeyUnavailable,
        "The erasure key could not be read from the OS credential store, so no erasure can be recorded until it can be.");

    internal static Error InvalidPreflightError { get; } = new(
        ErrorCodes.MemoryErasure.InvalidPreflight,
        "The erasure preflight token is invalid, expired, or has the wrong purpose.");

    private static readonly Error IdempotencyConflict = new(
        ErrorCodes.Security.IdempotencyConflict,
        "This mutation id already recorded a different erasure.");

    private static readonly MemoryReviewStore[] Stores =
        [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon];

    /// <summary>Refuses a catalog that cannot record an erasure: Core below 13, or no evidence tables.</summary>
    internal static async Task<Result> RequireInstalledAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken).ConfigureAwait(false)
            ? Result.Success()
            : Result.Failure(UnavailableError);
    }

    /// <summary>
    /// Opens the key for an erase prepare, creating it only when no evidence row exists in any store.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is inside a transaction.</exception>
    internal static async Task<Result<MemoryErasureKey>> OpenKeyForPrepareAsync(
        SqliteConnection connection,
        IMemoryErasureKeyCreator creator,
        MemoryReviewStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(creator);

        RequireStore(store);

        RequireOutsideTransaction(connection);

        bool evidenceRowsExist = await AnyEvidenceAsync(connection, cancellationToken).ConfigureAwait(false);

        MemoryErasureKeyOpenResult opened = creator.OpenOrCreate(evidenceRowsExist);

        // An absent key here is one the creator would not mint because rows exist: lost.
        return await AcceptAsync(connection, store, opened, KeyLostError, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the existing key for an erase apply, re-probing whatever the latch holds.</summary>
    /// <remarks>
    /// An absent key with evidence in any store is lost. An absent key with none means no prepare ever
    /// created one, so no token this installation issued can be honoured.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The connection is inside a transaction.</exception>
    internal static async Task<Result<MemoryErasureKey>> OpenKeyForApplyAsync(
        SqliteConnection connection,
        IMemoryErasureKeyProvider provider,
        MemoryReviewStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(provider);

        RequireStore(store);

        RequireOutsideTransaction(connection);

        MemoryErasureKeyOpenResult opened = provider.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        Error absent = opened.State is MemoryErasureKeyState.Absent
            && !await AnyEvidenceAsync(connection, cancellationToken).ConfigureAwait(false)
                ? InvalidPreflightError
                : KeyLostError;

        return await AcceptAsync(connection, store, opened, absent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Looks the erase up by its mutation id, before the transaction and again inside it.</summary>
    /// <returns>
    /// The stored receipt when its store and request digest match, null when none is recorded, or an
    /// idempotency conflict when the mutation id recorded a different erase.
    /// </returns>
    internal static async Task<Result<MemoryErasureReceiptRow?>> ProbeReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryReviewStore store,
        Guid mutationId,
        byte[] requestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(requestDigest);

        RequireStore(store);

        MemoryErasureReceiptRow? receipt = await MemoryErasureEvidence
            .ReadReceiptAsync(connection, transaction, mutationId, cancellationToken)
            .ConfigureAwait(false);

        if (receipt is null)
        {
            return Result<MemoryErasureReceiptRow?>.Success(null);
        }

        // FixedTimeEquals answers false for unequal lengths, and compares equal lengths in constant time.
        bool matches = receipt.Store == store
            && CryptographicOperations.FixedTimeEquals(receipt.RequestDigest, requestDigest);

        return matches
            ? Result<MemoryErasureReceiptRow?>.Success(receipt)
            : Result<MemoryErasureReceiptRow?>.Failure(IdempotencyConflict);
    }

    /// <summary>
    /// Scrubs the write-ahead log for a receipt still pending only on it, then reports the erase from
    /// its receipt.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is inside a transaction.</exception>
    internal static async Task<MemoryErasureResultDto> FinishAsync(
        SqliteConnection connection,
        MemoryErasureScrubber scrubber,
        MemoryErasureReceiptRow receipt,
        bool replayed,
        MemoryErasureNote[] notes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(scrubber);

        ArgumentNullException.ThrowIfNull(receipt);

        ArgumentNullException.ThrowIfNull(notes);

        if (InsideTransaction(connection))
        {
            throw new InvalidOperationException(
                "An erase is finished only after its transaction commits: the scrub runs outside every transaction of the request.");
        }

        MemoryErasureWalCheckpointAttempt attempt = MemoryErasureWalCheckpointAttempt.NotAttempted;

        MemoryErasureReceiptRow current = receipt;

        if (receipt.ScrubStateCode == ScrubPending && (receipt.ScrubPendingReasonMask & WalCheckpointPendingBit) != 0)
        {
            attempt = await scrubber.CheckpointAsync(cancellationToken).ConfigureAwait(false);

            if (attempt is MemoryErasureWalCheckpointAttempt.Truncated)
            {
                try
                {
                    _ = await MemoryErasureEvidence
                        .ClearWalPendingAsync(connection, null, receipt.MutationId, cancellationToken)
                        .ConfigureAwait(false);

                    current = await MemoryErasureEvidence
                        .ReadReceiptAsync(connection, null, receipt.MutationId, cancellationToken)
                        .ConfigureAwait(false) ?? receipt;
                }
                catch (SqliteException)
                {
                    // The erase committed and the log is truncated; only the receipt's upgrade failed.
                    // It stays pending for the log, and a replay or the scrub route finishes it.
                }
            }
        }

        return new MemoryErasureResultDto(
            current.Store,
            current.MutationId,
            replayed,
            Convert.ToHexStringLower(current.EffectDigest),
            new MemoryErasureLocalResultDto(
                current.ScrubStateCode == ScrubVerified
                    ? MemoryLocalErasureOutcome.Verified
                    : MemoryLocalErasureOutcome.RowsRemovedScrubPending,
                MemoryErasureScrubPendingReasons.FromMask(current.ScrubPendingReasonMask),
                attempt,
                current.ErasedItemCount,
                current.RemovedRowCount,
                current.RemovedLabelCount,
                current.RemovedRetirementSuppressionCount,
                SuppressionFingerprintRecorded: true),
            new MemoryErasureExternalExposureDto(
                MemoryExternalRevocation.NotPerformed,
                [
                    new(MemoryExternalChannel.InferenceProviderAuthorship, current.Authorship),
                    new(MemoryExternalChannel.InferenceProviderContext, current.Context),
                    new(MemoryExternalChannel.EmbeddingProvider, current.Embedding),
                    new(MemoryExternalChannel.EncryptedBackup, current.Backup),
                    new(MemoryExternalChannel.OtherExternal, current.OtherExternal),
                ]),
            MemoryRetainedLocalCopies.FromMask(current.RetainedCopiesMask),
            [.. notes]);
    }

    /// <summary>
    /// Keeps a present key only when it can verify the store's evidence, and maps every other state to
    /// its refusal.
    /// </summary>
    private static async Task<Result<MemoryErasureKey>> AcceptAsync(
        SqliteConnection connection,
        MemoryReviewStore store,
        MemoryErasureKeyOpenResult opened,
        Error absent,
        CancellationToken cancellationToken)
    {
        if (opened.State is not MemoryErasureKeyState.Present)
        {
            opened.Key?.Dispose();

            return opened.State is MemoryErasureKeyState.Absent ? absent : KeyUnavailableError;
        }

        MemoryErasureKey key = opened.Key
            ?? throw new InvalidOperationException("A present erasure key was opened without its material.");

        try
        {
            if (await MemoryErasureEvidence
                    .AnyForeignAsync(connection, null, store, key.KeyId.ToArray(), cancellationToken)
                    .ConfigureAwait(false))
            {
                key.Dispose();

                return KeyLostError;
            }
        }
        catch
        {
            key.Dispose();

            throw;
        }

        return key;
    }

    private static async Task<bool> AnyEvidenceAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        foreach (MemoryReviewStore store in Stores)
        {
            if (await MemoryErasureEvidence.AnyAsync(connection, null, store, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Refuses a connection inside a transaction, read from its own autocommit state, which a raw
    /// <c>BEGIN</c> changes as surely as a transaction object does.
    /// </summary>
    private static void RequireOutsideTransaction(SqliteConnection connection)
    {
        if (InsideTransaction(connection))
        {
            throw new InvalidOperationException(
                "Erasure key access can read the OS credential store, which is never read inside a SQLite transaction; open the key before the erase's transaction begins.");
        }
    }

    private static bool InsideTransaction(SqliteConnection connection) =>
        connection.State == System.Data.ConnectionState.Open
        && SQLitePCL.raw.sqlite3_get_autocommit(connection.Handle) == 0;

    private static void RequireStore(MemoryReviewStore store)
    {
        if (store is not (MemoryReviewStore.Covenant or MemoryReviewStore.Saga or MemoryReviewStore.Lexicon))
        {
            throw new ArgumentOutOfRangeException(nameof(store), store, "An erase belongs to a recognized store.");
        }
    }
}

/// <summary>The scope-boundary and availability notes an erase states, in code order.</summary>
/// <remarks>
/// A Global Covenant key stays proposable by agents inside Campaigns; an unresolved Saga scope stops
/// matching once its Session's Campaign is resolved; erasure in one scope never reaches another; and a
/// Covenant erase drains in-flight turns. A reclaimed Covenant key adds no note: its consequences are
/// carried by the plan's own facts.
/// </remarks>
internal static class MemoryErasureNotes
{
    internal static MemoryErasureNote[] For(MemoryReviewStore store, MemoryErasureScopeKind scope, bool reclaimsKey)
    {
        bool validScope = store switch
        {
            MemoryReviewStore.Saga => scope is MemoryErasureScopeKind.Unclassified
                or MemoryErasureScopeKind.Global
                or MemoryErasureScopeKind.Campaign
                or MemoryErasureScopeKind.LegacyUnresolved,
            MemoryReviewStore.Lexicon or MemoryReviewStore.Covenant =>
                scope is MemoryErasureScopeKind.Global or MemoryErasureScopeKind.Campaign,
            _ => throw new ArgumentOutOfRangeException(nameof(store), store, "Notes are stated for a recognized store."),
        };

        if (!validScope)
        {
            throw new ArgumentException("This store has no such scope.", nameof(scope));
        }

        if (reclaimsKey && store is not MemoryReviewStore.Covenant)
        {
            throw new ArgumentException("Only a Covenant erase can reclaim its key.", nameof(reclaimsKey));
        }

        List<MemoryErasureNote> notes = [];

        if (store is MemoryReviewStore.Covenant && scope is MemoryErasureScopeKind.Global)
        {
            notes.Add(MemoryErasureNote.GlobalKeyStillProposableInCampaigns);
        }

        if (store is MemoryReviewStore.Saga && scope is MemoryErasureScopeKind.LegacyUnresolved)
        {
            notes.Add(MemoryErasureNote.UnresolvedScopeStopsMatchingOnResolution);
        }

        notes.Add(MemoryErasureNote.OtherScopesUnaffected);

        if (store is MemoryReviewStore.Covenant)
        {
            notes.Add(MemoryErasureNote.CovenantDrainsInFlightTurns);
        }

        return [.. notes];
    }
}
