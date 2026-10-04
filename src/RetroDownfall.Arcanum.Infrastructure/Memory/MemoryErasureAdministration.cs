using System.Data;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Secrets.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Memory;

/// <summary>
/// The erasure status, the write-ahead-log scrub retry, and the recovery of a lost or replaced erasure
/// key (spec §5.7).
/// </summary>
/// <remarks>
/// <para><b>Status</b> is a read. Below Core 13 there is no evidence (§5.4). With no evidence rows, the
/// key's state comes from a metadata-only presence probe, so an installation that never erased anything
/// is reported without the key's bytes being asked for; an item the probe finds is still reported as
/// unavailable when this process's last read of it found it malformed or unreadable. With rows, the key is re-probed as every operator
/// call re-probes it, and the rows are counted against it: a row the key did not record is unverifiable,
/// and every row is unverifiable when the key is gone, which is a lost key. When the key cannot be read
/// at all, nothing can be said about which rows it would verify, so their unverifiable counts read as
/// zero, meaning unknown.</para>
///
/// <para><b>The scrub</b> reads which receipts are still pending on the log before it checkpoints, outside
/// any transaction, and only a checkpoint that truncated clears that one reason, and only on those
/// receipts. A busy or unavailable checkpoint changes nothing, and a receipt that committed while the
/// checkpoint ran is not one the checkpoint can be said to have covered. The other reasons never
/// clear.</para>
///
/// <para><b>Key reset</b> is prepared and then applied with the preview's five-minute token, which binds
/// the key's state and the unverifiable fingerprints and receipts of each store. Apply re-probes the key
/// outside every transaction. A key that cannot be read, or an item that is not a key, refuses before
/// anything is written or deleted, and a malformed item is never overwritten. A present key is kept. A
/// proven absence creates a key, but only for a plan that was itself made without one. Then, inside one
/// <c>BEGIN IMMEDIATE</c>, the evidence the current key cannot verify is measured again: nothing left to
/// discard is success, so a second apply is harmless; any store's count that differs from the preview is
/// a stale plan; otherwise exactly those rows go, receipts with their subjects.</para>
///
/// <para><b>Why the key comes first.</b> The keychain is never touched inside a transaction, so the
/// create and the delete cannot be one step. Creating first means a crash between them leaves a present
/// key and rows it cannot verify: the same fail-closed state as a replaced key, which status reports and
/// a fresh prepare and apply finishes. The token does not survive a crash, because its key lives only in
/// this process. The same state follows a reset that creates the key and then finds the counts changed:
/// the new key stays, nothing is discarded, and the reset answers that its plan is stale. Deleting first
/// would discard the evidence before the key that would let the installation record new evidence was
/// known to exist.</para>
///
/// <para>Every log line carries an attempt, a count or a flag, never a key, a key identifier, a
/// fingerprint, a name or content.</para>
/// </remarks>
internal sealed class MemoryErasureAdministration(
    ArcanumDbContext db,
    MemoryErasureKeyring keyring,
    IMemoryErasureTokenCodec tokens,
    MemoryErasureScrubber scrubber,
    ILogger<MemoryErasureAdministration> logger) : IMemoryErasureAdministration
{
    private static readonly Error KeyUnavailable = new(
        ErrorCodes.MemoryErasure.KeyUnavailable,
        "The erasure key could not be read, or the stored item is not a valid key, so nothing was discarded and no key was written. "
        + "If the credential store is locked or did not answer, unlock it and prepare the reset again. Only if that persists and the "
        + "stored erasure key item is confirmed malformed, remove it with the OS credential tool and prepare the reset again: removing a "
        + "key makes every erasure fingerprint unverifiable, and the reset discards them, so erased content could be learned again.");

    private static readonly Error StalePlan = new(
        ErrorCodes.MemoryErasure.StalePlan,
        "The erasure key's state, or the evidence it cannot verify, changed after this reset was prepared, so nothing was discarded; prepare it again.");

    private static readonly MemoryReviewStore[] StoreOrder =
        [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon];

    private readonly Func<CancellationToken, Task<MemoryErasureWalCheckpointAttempt>> _checkpoint =
        (scrubber ?? throw new ArgumentNullException(nameof(scrubber))).CheckpointAsync;

    /// <summary>The same service with the scrub's checkpoint replaced, for tests that time it.</summary>
    internal MemoryErasureAdministration(
        ArcanumDbContext db,
        MemoryErasureKeyring keyring,
        IMemoryErasureTokenCodec tokens,
        MemoryErasureScrubber scrubber,
        ILogger<MemoryErasureAdministration> logger,
        Func<CancellationToken, Task<MemoryErasureWalCheckpointAttempt>> checkpoint)
        : this(db, keyring, tokens, scrubber, logger)
    {
        _checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }

    public async Task<Result<MemoryErasureStatusDto>> GetStatusAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Counted with no key, every row is unverifiable; below Core 13 there are none to count.
        MemoryErasureEvidenceCounts unkeyed = await MemoryErasureEvidence
            .CountAsync(connection, null, null, cancellationToken)
            .ConfigureAwait(false);

        if (!HasRows(unkeyed))
        {
            return new MemoryErasureStatusDto(ProbeWithoutEvidence(), [.. unkeyed.Stores], unkeyed.PendingScrubReceipts);
        }

        // The operator re-probe, outside every transaction, which publishes what it finds into the latch.
        MemoryErasureKeyOpenResult opened = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        using MemoryErasureKey? key = opened.Key;

        switch (opened.State)
        {
            case MemoryErasureKeyState.Present:
                MemoryErasureEvidenceCounts keyed = await MemoryErasureEvidence
                    .CountAsync(connection, null, RequireKey(key).KeyId.ToArray(), cancellationToken)
                    .ConfigureAwait(false);

                return new MemoryErasureStatusDto(MemoryErasureKeyStatus.Present, [.. keyed.Stores], keyed.PendingScrubReceipts);

            case MemoryErasureKeyState.Absent:
                return new MemoryErasureStatusDto(Keyless(unkeyed), [.. unkeyed.Stores], unkeyed.PendingScrubReceipts);

            default:
                return new MemoryErasureStatusDto(
                    MemoryErasureKeyStatus.Unavailable,
                    [.. unkeyed.Stores.Select(static store => store with { Unverifiable = 0 })],
                    unkeyed.PendingScrubReceipts);
        }
    }

    public async Task<Result<MemoryErasureScrubResultDto>> ScrubAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken).ConfigureAwait(false))
        {
            return MemoryErasureProtocol.UnavailableError;
        }

        // Before the checkpoint and outside any transaction: these are the only receipts a truncation the
        // checkpoint reports can be said to have covered.
        IReadOnlyList<Guid> pending = await MemoryErasureEvidence
            .ReadWalPendingAsync(connection, null, cancellationToken)
            .ConfigureAwait(false);

        MemoryErasureWalCheckpointAttempt attempt = MemoryErasureWalCheckpointAttempt.NotAttempted;

        long verified = 0;

        if (pending.Count > 0)
        {
            attempt = await _checkpoint(cancellationToken).ConfigureAwait(false);

            if (attempt is MemoryErasureWalCheckpointAttempt.Truncated)
            {
                verified = await SqliteBusyRetry.ExecuteAsync(
                    async () =>
                    {
                        // A fresh BEGIN IMMEDIATE per attempt, so a busy retry never reuses a rolled-back one.
                        await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                        long cleared = 0;

                        foreach (Guid mutationId in pending)
                        {
                            cleared += await MemoryErasureEvidence
                                .ClearWalPendingAsync(connection, transaction, mutationId, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                        return cleared;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        MemoryErasureEvidenceCounts counts = await MemoryErasureEvidence
            .CountAsync(connection, null, null, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Erasure scrub checkpoint attempt {WalCheckpointAttempt}: {Verified} receipts verified, {StillPending} still pending.",
            attempt,
            verified,
            counts.PendingScrubReceipts);

        return new MemoryErasureScrubResultDto(attempt, verified, counts.PendingScrubReceipts);
    }

    public async Task<Result<MemoryErasureKeyResetPreflightDto>> PrepareKeyResetAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken).ConfigureAwait(false))
        {
            return MemoryErasureProtocol.UnavailableError;
        }

        MemoryErasureKeyOpenResult opened = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        using MemoryErasureKey? key = opened.Key;

        MemoryErasureKeyStatus status;

        MemoryErasureEvidenceCounts counts;

        switch (opened.State)
        {
            case MemoryErasureKeyState.Present:
                status = MemoryErasureKeyStatus.Present;

                counts = await MemoryErasureEvidence
                    .CountAsync(connection, null, RequireKey(key).KeyId.ToArray(), cancellationToken)
                    .ConfigureAwait(false);

                break;

            case MemoryErasureKeyState.Absent:
                counts = await MemoryErasureEvidence
                    .CountAsync(connection, null, null, cancellationToken)
                    .ConfigureAwait(false);

                status = Keyless(counts);

                break;

            default:
                return KeyUnavailable;
        }

        Result<MemoryErasureIssuedToken> issued = tokens.IssueErasureKeyReset(Facts(status, counts));

        if (issued.IsFailure)
        {
            return issued.Error;
        }

        return new MemoryErasureKeyResetPreflightDto(
            status,
            [.. counts.Stores],
            issued.Value.IssuedAtUtc,
            issued.Value.ExpiresAtUtc,
            issued.Value.Token);
    }

    public async Task<Result<MemoryErasureKeyResetResultDto>> ResetKeyAsync(
        MemoryErasureKeyResetRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (!await MemoryErasureEvidence.IsInstalledAsync(connection, null, cancellationToken).ConfigureAwait(false))
        {
            return MemoryErasureProtocol.UnavailableError;
        }

        Result<MemoryErasureKeyResetTokenFacts> read = tokens.ReadErasureKeyReset(request.PreflightToken);

        if (read.IsFailure)
        {
            return MemoryErasureProtocol.InvalidPreflightError;
        }

        MemoryErasureKeyResetTokenFacts plan = read.Value;

        // Outside every transaction: the only keychain I/O a reset makes, and all of it before any row
        // is touched.
        MemoryErasureKeyOpenResult opened = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        bool created = false;

        if (opened.State is MemoryErasureKeyState.Absent)
        {
            opened.Key?.Dispose();

            // A key is created only for a plan that was itself made without one. A plan made while a key
            // was present previewed what that key could not verify, which says nothing about a new one.
            if (plan.KeyStatus is not (MemoryErasureKeyStatus.Lost or MemoryErasureKeyStatus.Absent))
            {
                return StalePlan;
            }

            // Created is what the keyring wrote, not what it found: another caller may have written the
            // key since the read above.
            opened = keyring.CreateForReset(out created);
        }

        if (opened.State is not MemoryErasureKeyState.Present)
        {
            opened.Key?.Dispose();

            return KeyUnavailable;
        }

        using MemoryErasureKey key = RequireKey(opened.Key);

        byte[] keyId = key.KeyId.ToArray();

        (long Fingerprints, long Receipts, bool WalPendingDiscarded)? discarded = await SqliteBusyRetry.ExecuteAsync<(long Fingerprints, long Receipts, bool WalPendingDiscarded)?>(
            async () =>
            {
                await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                MemoryErasureKeyResetTokenFacts measured = Facts(
                    plan.KeyStatus,
                    await MemoryErasureEvidence.CountAsync(connection, transaction, keyId, cancellationToken).ConfigureAwait(false));

                // Nothing this key cannot verify is left, so a reset that already ran is a success.
                if (measured == Facts(plan.KeyStatus, null))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return (0, 0, false);
                }

                // Store by store: rows that moved between stores are a different reset, whatever the totals.
                if (measured != plan)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                    return null;
                }

                // Which receipts are still pending on the log, before and after, so the reset can tell whether
                // it discarded the debt of one: nothing else will retry a receipt that is gone.
                IReadOnlyList<Guid> pendingBefore = await MemoryErasureEvidence
                    .ReadWalPendingAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);

                (long Fingerprints, long Receipts) deleted = await MemoryErasureEvidence
                    .DeleteUnverifiableAsync(connection, transaction, keyId, cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyList<Guid> pendingAfter = await MemoryErasureEvidence
                    .ReadWalPendingAsync(connection, transaction, cancellationToken)
                    .ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return (deleted.Fingerprints, deleted.Receipts, pendingBefore.Any(id => !pendingAfter.Contains(id)));
            },
            cancellationToken).ConfigureAwait(false);

        if (discarded is not { } counts)
        {
            return StalePlan;
        }

        logger.LogInformation(
            "Erasure key reset discarded {FingerprintsDiscarded} fingerprints and {ReceiptsDiscarded} receipts; key created: {KeyCreated}.",
            counts.Fingerprints,
            counts.Receipts,
            created);

        if (counts.WalPendingDiscarded)
        {
            await CheckpointAfterResetAsync().ConfigureAwait(false);
        }

        return new MemoryErasureKeyResetResultDto(MemoryErasureKeyStatus.Present, counts.Fingerprints, counts.Receipts, created);
    }

    /// <summary>
    /// The one checked checkpoint a reset runs after its commit, when it discarded a receipt that was still
    /// pending on the write-ahead log, with the outcome logged and never returned.
    /// </summary>
    /// <remarks>
    /// The discarded receipt was the only thing tracking that scrub, so without this the erased frames could
    /// sit in the log with nothing left to retry them. It is the same checkpoint the scrub runs, after the
    /// commit and with <see cref="CancellationToken.None"/>, so it can never fail a reset that has already
    /// committed: a busy or unavailable checkpoint, or one that faults, is reported as what it was. A
    /// checkpoint that was busy changes nothing, and the log is truncated by the next one that finds no
    /// reader.
    /// </remarks>
    private async Task CheckpointAfterResetAsync()
    {
        MemoryErasureWalCheckpointAttempt attempt;

        try
        {
            attempt = await _checkpoint(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            attempt = MemoryErasureWalCheckpointAttempt.Unavailable;
        }

        logger.LogInformation("Erasure key reset write-ahead-log checkpoint attempt: {WalCheckpointAttempt}.", attempt);
    }

    private static bool HasRows(MemoryErasureEvidenceCounts counts) =>
        counts.Stores.Any(static store => store.Fingerprints > 0 || store.Receipts > 0);

    /// <summary>With no key, a fingerprint is evidence nothing can verify: the key is lost, not merely absent.</summary>
    private static MemoryErasureKeyStatus Keyless(MemoryErasureEvidenceCounts counts) =>
        counts.Stores.Any(static store => store.Fingerprints > 0)
            ? MemoryErasureKeyStatus.Lost
            : MemoryErasureKeyStatus.Absent;

    /// <summary>
    /// The key's state when no evidence exists, from the metadata-only probe where the store has one, so
    /// no secret is read. A store without one is asked through the operator re-probe instead.
    /// </summary>
    /// <remarks>
    /// That an item exists says nothing about whether it holds a key, so an item the probe finds is read
    /// against the latch, which costs no I/O: when this process last read the item and found it
    /// malformed or could not read it, that is still the answer. A newer probe that finds no item at all
    /// overrides it, because the item has gone.
    /// </remarks>
    private MemoryErasureKeyStatus ProbeWithoutEvidence()
    {
        if (keyring.ProbePresence() is { } presence)
        {
            return presence switch
            {
                OsCredentialStoreStatus.Ok => keyring.Latch.State is MemoryErasureKeyState.Malformed or MemoryErasureKeyState.Unavailable
                    ? MemoryErasureKeyStatus.Unavailable
                    : MemoryErasureKeyStatus.Present,
                OsCredentialStoreStatus.NotFound => MemoryErasureKeyStatus.Absent,
                _ => MemoryErasureKeyStatus.Unavailable,
            };
        }

        MemoryErasureKeyOpenResult opened = keyring.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        opened.Key?.Dispose();

        return opened.State switch
        {
            MemoryErasureKeyState.Present => MemoryErasureKeyStatus.Present,
            MemoryErasureKeyState.Absent => MemoryErasureKeyStatus.Absent,
            _ => MemoryErasureKeyStatus.Unavailable,
        };
    }

    /// <summary>
    /// What a reset token binds: the key's state and each store's unverifiable fingerprints and receipts.
    /// Null counts are none in every store.
    /// </summary>
    private static MemoryErasureKeyResetTokenFacts Facts(MemoryErasureKeyStatus status, MemoryErasureEvidenceCounts? counts)
    {
        if (counts is null)
        {
            return new MemoryErasureKeyResetTokenFacts(status, 0, 0, 0, 0, 0, 0);
        }

        if (!counts.Stores.Select(static store => store.Store).SequenceEqual(StoreOrder)
            || counts.UnverifiableStoreReceipts.Count != StoreOrder.Length)
        {
            throw new InvalidOperationException("Erasure evidence is counted for every store, in store-code order.");
        }

        return new MemoryErasureKeyResetTokenFacts(
            status,
            counts.Stores[0].Unverifiable,
            counts.Stores[1].Unverifiable,
            counts.Stores[2].Unverifiable,
            counts.UnverifiableStoreReceipts[0],
            counts.UnverifiableStoreReceipts[1],
            counts.UnverifiableStoreReceipts[2]);
    }

    private static MemoryErasureKey RequireKey(MemoryErasureKey? key) =>
        key ?? throw new InvalidOperationException("A present erasure key was opened without its material.");

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }
}
