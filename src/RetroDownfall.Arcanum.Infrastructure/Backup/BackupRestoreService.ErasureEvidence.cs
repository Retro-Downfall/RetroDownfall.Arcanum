using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// The restore's read of this installation's erasure evidence: what was erased here must not come back
/// with an archive restored over it.
/// </summary>
internal sealed partial class BackupRestoreService
{
    private const string NewProfileRootErasureWarning =
        "This installation's erasure evidence is not applied to a new profile root; items erased here can "
        + "reappear there.";

    /// <summary>
    /// Reads the destination's fingerprints, receipts and receipt subjects as values, and proves them
    /// against the erasure key.
    /// </summary>
    /// <remarks>
    /// <para>The Grimoire is read first, in one <c>BEGIN DEFERRED</c> on a read-only handle that is closed
    /// before anything else happens, so a destination that never erased anything is planned without a
    /// single credential call, and no keychain call ever runs inside a transaction. The key is consulted
    /// only when the Grimoire holds rows, or when it cannot say whether it does.</para>
    ///
    /// <para>The key is the anchor. With rows, only the key that recorded every one of them proves them; a
    /// missing or replaced key, or a keychain that cannot answer, refuses. Without a readable Grimoire,
    /// only a keychain that proves the key absent proves there is nothing to lose, because evidence is
    /// committed only under a key; anything else refuses. No branch reads an unanswerable question as
    /// "nothing was erased".</para>
    ///
    /// <para>The key is re-probed unless it is already latched Present. A Present latch is kept for the
    /// life of the process, so the key a plan proved is the key a later read in the same restore holds.
    /// The key is always disposed here; only its identifier travels on.</para>
    /// </remarks>
    private async Task<BackupRestoreErasureEvidence> ReadDestinationErasureEvidenceAsync(
        CancellationToken cancellationToken)
    {
        bool readable = false;

        MemoryErasureEvidenceSnapshot? snapshot = null;

        if (File.Exists(_paths.DatabasePath))
        {
            try
            {
                SecretStoreReadResult secret = await _secretStore
                    .GetGrimoireEncryptionSecretReadResultAsync()
                    .ConfigureAwait(false);

                if (secret.Status == SecretStoreReadStatus.Ok && !string.IsNullOrEmpty(secret.Value))
                {
                    await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                        .OpenAsync(_paths.DatabasePath, secret.Value, readOnly: true, cancellationToken)
                        .ConfigureAwait(false);

                    await using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);

                    snapshot = await MemoryErasureEvidence
                        .ReadSnapshotAsync(connection, transaction, cancellationToken)
                        .ConfigureAwait(false);

                    readable = true;
                }
            }
            // The same list as the Campaign read: opening the destination starts at its key-derivation
            // sidecar, so a missing, malformed or unsupported sidecar is "this Grimoire cannot answer" too.
            catch (Exception exception) when (
                exception is SqliteException
                    or InvalidDataException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or FormatException
                    or System.Text.Json.JsonException
                    or System.Security.Cryptography.CryptographicException)
            {
                readable = false;

                snapshot = null;
            }
        }

        return readable
            ? ProveRows(snapshot ?? MemoryErasureEvidenceSnapshot.Empty)
            : ProveNothingCommitted();
    }

    /// <summary>A readable Grimoire's rows, proven by the key that recorded them or refused.</summary>
    /// <remarks>
    /// A receipt with no fingerprint is still evidence: release removes a fingerprint and keeps the
    /// record of the erasure.
    /// </remarks>
    private BackupRestoreErasureEvidence ProveRows(MemoryErasureEvidenceSnapshot rows)
    {
        if (!rows.HasRows)
        {
            return BackupRestoreErasureEvidence.None;
        }

        MemoryErasureKeyOpenResult opened = _erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        using MemoryErasureKey? key = opened.Key;

        return opened.State switch
        {
            MemoryErasureKeyState.Present when key is not null && RecordedBy(rows, key) =>
                BackupRestoreErasureEvidence.Present(key.KeyId.ToArray(), rows),
            MemoryErasureKeyState.Present or MemoryErasureKeyState.Absent =>
                BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.KeyMissing, rows),
            _ => BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.KeyUnavailable, rows),
        };
    }

    /// <summary>
    /// A Grimoire that is absent or cannot be read: only a keychain that proves the key absent proves no
    /// evidence was ever committed.
    /// </summary>
    private BackupRestoreErasureEvidence ProveNothingCommitted()
    {
        MemoryErasureKeyOpenResult opened = _erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        opened.Key?.Dispose();

        return opened.State is MemoryErasureKeyState.Absent
            ? BackupRestoreErasureEvidence.None
            : BackupRestoreErasureEvidence.Refused(BackupRestoreErasureCodes.EvidenceUnavailable, null);
    }

    /// <summary>Whether this installation holds an erasure key, which it has once anything was erased here.</summary>
    private bool HoldsErasureKey()
    {
        MemoryErasureKeyOpenResult opened = _erasureKeys.OpenExisting(MemoryErasureKeyProbe.Reprobe);

        opened.Key?.Dispose();

        return opened.State is MemoryErasureKeyState.Present;
    }

    private static bool RecordedBy(MemoryErasureEvidenceSnapshot rows, MemoryErasureKey key) =>
        rows.Fingerprints.All(row => key.HasKeyId(row.KeyId))
        && rows.Receipts.All(row => key.HasKeyId(row.KeyId));
}
