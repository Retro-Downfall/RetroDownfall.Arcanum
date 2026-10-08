using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Memory;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>One erasure receipt, column for column.</summary>
/// <remarks>
/// Both digests are keyed and bind only identifiers, counts and flags, never content, so a receipt can
/// be read back and replayed without it ever becoming a way to test a guess about what was erased.
/// <see cref="ScrubStateCode"/> is 1 while any reason is pending and 2 once none is, and
/// <see cref="ScrubPendingReasonMask"/> holds one bit per <see cref="MemoryErasureScrubPendingReason"/>.
/// </remarks>
internal sealed record MemoryErasureReceiptRow(
    Guid MutationId,
    MemoryReviewStore Store,
    byte[] KeyId,
    byte[] RequestDigest,
    byte[] EffectDigest,
    int ErasedItemCount,
    long RemovedRowCount,
    int RemovedLabelCount,
    int RemovedRetirementSuppressionCount,
    MemoryExternalEvidence Authorship,
    MemoryExternalEvidence Context,
    MemoryExternalEvidence Embedding,
    MemoryExternalEvidence Backup,
    MemoryExternalEvidence OtherExternal,
    int RetainedCopiesMask,
    int ScrubStateCode,
    int ScrubPendingReasonMask);

/// <summary>Content-free counts of the evidence the installation holds.</summary>
/// <param name="Stores">Always three entries, in store-code order: Covenant, Saga, Lexicon.</param>
/// <param name="UnverifiableReceipts">Receipts recorded under a key other than the current one.</param>
/// <param name="PendingScrubReceipts">Receipts whose scrub is still pending, whatever key wrote them.</param>
/// <param name="UnverifiableStoreReceipts">
/// <see cref="UnverifiableReceipts"/> split by store: three entries, aligned with <see cref="Stores"/>.
/// </param>
internal sealed record MemoryErasureEvidenceCounts(
    IReadOnlyList<MemoryErasureStoreCountsDto> Stores,
    long UnverifiableReceipts,
    long PendingScrubReceipts,
    IReadOnlyList<long> UnverifiableStoreReceipts);

/// <summary>One erasure fingerprint, column for column.</summary>
internal sealed record MemoryErasureFingerprintRow(byte[] Fingerprint, MemoryReviewStore Store, byte[] KeyId);

/// <summary>One subject an erasure receipt names, column for column.</summary>
internal sealed record MemoryErasureReceiptSubjectRow(Guid MutationId, byte[] SubjectDigest);

/// <summary>Every evidence row an installation holds, read as values in one snapshot.</summary>
/// <remarks>
/// Values rather than a handle, so a restore can carry a destination's evidence across work on another
/// database without holding the destination open. Each list is in primary-key order.
/// </remarks>
internal sealed record MemoryErasureEvidenceSnapshot(
    IReadOnlyList<MemoryErasureFingerprintRow> Fingerprints,
    IReadOnlyList<MemoryErasureReceiptRow> Receipts,
    IReadOnlyList<MemoryErasureReceiptSubjectRow> Subjects)
{
    /// <summary>A catalog that holds no evidence.</summary>
    internal static MemoryErasureEvidenceSnapshot Empty { get; } = new([], [], []);

    /// <summary>
    /// Whether any fingerprint or receipt exists. A receipt alone counts: its fingerprint may have been
    /// released while the record of the erasure was kept.
    /// </summary>
    internal bool HasRows => Fingerprints.Count > 0 || Receipts.Count > 0;
}

/// <summary>
/// A <c>COMMIT</c> that failed in a way that may still have persisted, and whether the rollback that
/// followed it failed too.
/// </summary>
/// <remarks>
/// Every store's apply raises this for a commit failure that is not SQLite's busy answer, because the
/// frame can reach the log before the failure is reported. Its caller then reads the receipt back on a
/// connection of its own: a receipt is an erase that happened, and its absence is the commit's own
/// failure.
/// </remarks>
internal sealed class UncertainCommitException(Exception commitFailure, bool rollbackFailed = false)
    : Exception("The erase's commit failed, and its outcome is settled by its receipt.", commitFailure)
{
    /// <summary>Whether the rollback that followed the failed commit failed as well.</summary>
    internal bool RollbackFailed { get; } = rollbackFailed;
}

/// <summary>
/// The only reader and writer of the erasure evidence tables: fingerprints, receipts, and receipt
/// subjects.
/// </summary>
/// <remarks>
/// <para>Every member runs on the caller's connection and, when it passes one, the caller's
/// transaction. Nothing here begins, commits, or opens anything, and nothing here reads the erasure
/// key; the caller computes every digest.</para>
///
/// <para>Evidence can only be committed at Core version 13 or later, because the erase routes require
/// it and a restore that applies evidence drains to head first. So a catalog recorded below 13, or one
/// without the fingerprint table, holds no evidence: every read answers empty and every delete removes
/// nothing, which is what keeps writes flowing during a live upgrade while an earlier sweep drains.
/// The two inserts refuse instead, since recording evidence a catalog cannot hold would be a lie about
/// what it suppresses. A catalog that has the fingerprint table but missing or malformed version
/// metadata throws, so a catalog that could hold evidence but cannot say what it is fails closed. The
/// one exception is restore staging's <see cref="ReplaceAllAsync"/>, which goes by the tables rather than
/// the recorded version, so no archived row survives it to become visible after a later upgrade.</para>
///
/// <para>These are the only statements in the product that delete, update, or insert evidence rows,
/// and architecture tests pin that, over the source and over the strings the compiled assemblies load:
/// a later operation that needs a new evidence write adds a member here. Nothing replaces or upserts a
/// row, because a replace deletes the old row, which cascades to a receipt's subjects and never fires the
/// receipt table's update guard. Nothing else in the product names the tables at all; the retention
/// inventory reads its counts through <see cref="ReadInventoryAsync"/>.</para>
/// </remarks>
internal static class MemoryErasureEvidence
{
    /// <summary>The first Core version whose catalog holds the evidence tables.</summary>
    internal const int CoreSchemaVersion = 13;

    private const int KeyIdBytes = MemoryErasureDigestGrammar.KeyIdBytes;

    private const int DigestBytes = MemoryErasureDigestGrammar.DigestBytes;

    private const int ScrubVerified = 2;

    /// <summary>Every store, in code order, which is the order counts are reported in.</summary>
    private static readonly MemoryReviewStore[] Stores =
        [MemoryReviewStore.Covenant, MemoryReviewStore.Saga, MemoryReviewStore.Lexicon];

    /// <summary>The positive installation answers, per native connection, with the stamp each was kept under.</summary>
    private static readonly ConditionalWeakTable<sqlite3, InstalledCatalog> InstalledCatalogs = new();

    /// <summary>
    /// Whether the connection is inside a transaction, read from its own autocommit state, which a raw
    /// <c>BEGIN</c> changes as surely as a transaction object does.
    /// </summary>
    internal static bool InTransaction(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.State == System.Data.ConnectionState.Open
            && raw.sqlite3_get_autocommit(connection.Handle) == 0;
    }

    /// <summary>Whether this catalog can hold evidence: Core 13 or later, with the fingerprint table.</summary>
    /// <remarks>
    /// The table is asked for first. A catalog without it holds no evidence whatever its metadata says,
    /// and a Covenant-only catalog carries no Core metadata to read at all. Only a catalog that has the
    /// table must say which Core version it is, and missing or malformed metadata there still throws.
    ///
    /// <para>A positive answer read outside any transaction is kept per native connection with the
    /// catalog's change stamp, and reused while that stamp reads the same, so a repeated probe is one
    /// statement rather than two. The stamp is the schema cookie (DDL, by this connection or any other),
    /// the data version (a commit by another connection) and this connection's total change count (a row
    /// this connection wrote, whether or not it was later committed). Read outside a transaction, the
    /// answer describes committed state, and every statement that could change the fingerprint table or
    /// the recorded Core version moves one of the three before the answer is reused.</para>
    ///
    /// <para>An answer read inside a transaction is returned but never kept. A rollback lowers neither the
    /// change count nor the data version, so a stamp read inside a transaction that then rolled back could
    /// read the same again after the rollback had undone the very version the answer vouched for. A
    /// negative answer is never kept either: it is the cheap one, and the catalog it describes is the one
    /// an upgrade is about to change.</para>
    /// </remarks>
    internal static async Task<bool> IsInstalledAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        CatalogStamp stamp = await ReadCatalogStampAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        sqlite3 handle = connection.Handle
            ?? throw new InvalidOperationException("The connection has no open database handle.");

        if (InstalledCatalogs.TryGetValue(handle, out InstalledCatalog? installed) && installed.Stamp == stamp)
        {
            return true;
        }

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'memory_erasure_fingerprints');"))
        {
            if (!await ExistsAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        bool current = await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken, transaction).ConfigureAwait(false)
            >= CoreSchemaVersion;

        if (current && !InTransaction(connection))
        {
            InstalledCatalogs.GetOrCreateValue(handle).Stamp = stamp;
        }

        return current;
    }

    /// <summary>The last stamp a positive installation answer was kept under, for one native connection.</summary>
    /// <remarks>A native connection is used by one caller at a time, so the slot needs no lock.</remarks>
    private sealed class InstalledCatalog
    {
        internal CatalogStamp? Stamp { get; set; }
    }

    /// <summary>What has to read the same for a positive installation answer to still hold.</summary>
    private sealed record CatalogStamp(long SchemaCookie, long DataVersion, long TotalChanges);

    private static async Task<CatalogStamp> ReadCatalogStampAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT (SELECT schema_version FROM pragma_schema_version), (SELECT data_version FROM pragma_data_version), total_changes();");

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CatalogStamp(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
            : throw new InvalidDataException("The catalog change stamp could not be read.");
    }

    /// <summary>Whether <paramref name="store"/> holds any fingerprint at all, under any key.</summary>
    internal static async Task<bool> AnyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryReviewStore store,
        CancellationToken cancellationToken)
    {
        RequireStore(store);

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM memory_erasure_fingerprints WHERE StoreCode = $store);");

        _ = command.Parameters.AddWithValue("$store", (int)store);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="store"/> holds a fingerprint recorded under any key but
    /// <paramref name="keyId"/>: evidence this key can never match, so its store must fail closed.
    /// </summary>
    internal static async Task<bool> AnyForeignAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryReviewStore store,
        byte[] keyId,
        CancellationToken cancellationToken)
    {
        RequireStore(store);

        RequireLength(keyId, KeyIdBytes, nameof(keyId));

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM memory_erasure_fingerprints WHERE StoreCode = $store AND KeyId <> $keyId);");

        _ = command.Parameters.AddWithValue("$store", (int)store);

        _ = command.Parameters.AddWithValue("$keyId", keyId);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether this exact fingerprint is recorded.</summary>
    internal static async Task<bool> ContainsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        RequireLength(fingerprint, DigestBytes, nameof(fingerprint));

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM memory_erasure_fingerprints WHERE Fingerprint = $fingerprint);");

        _ = command.Parameters.AddWithValue("$fingerprint", fingerprint);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one fingerprint.</summary>
    /// <returns>False when the fingerprint was already recorded, which leaves the existing row as it was.</returns>
    /// <exception cref="InvalidOperationException">The catalog cannot hold evidence.</exception>
    internal static async Task<bool> InsertFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        byte[] fingerprint,
        MemoryReviewStore store,
        byte[] keyId,
        CancellationToken cancellationToken)
    {
        RequireLength(fingerprint, DigestBytes, nameof(fingerprint));

        RequireStore(store);

        RequireLength(keyId, KeyIdBytes, nameof(keyId));

        await RequireInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        // OR IGNORE also skips a row that fails a CHECK, which is why every length and code is refused
        // above: the only conflict left for it to absorb is the primary key.
        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            INSERT OR IGNORE INTO memory_erasure_fingerprints (Fingerprint, StoreCode, KeyId)
            VALUES ($fingerprint, $store, $keyId);
            """);

        _ = command.Parameters.AddWithValue("$fingerprint", fingerprint);

        _ = command.Parameters.AddWithValue("$store", (int)store);

        _ = command.Parameters.AddWithValue("$keyId", keyId);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Removes one fingerprint.</summary>
    /// <returns>The rows removed: one, or zero when it was not recorded.</returns>
    internal static async Task<int> DeleteFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        RequireLength(fingerprint, DigestBytes, nameof(fingerprint));

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            "DELETE FROM memory_erasure_fingerprints WHERE Fingerprint = $fingerprint;");

        _ = command.Parameters.AddWithValue("$fingerprint", fingerprint);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one receipt and one subject row per erased subject.</summary>
    /// <remarks>
    /// <para>The mutation id is stored in its governed spelling, upper-case and dashed. A receipt that
    /// already exists is a primary-key failure, not a silent no-op: the erase protocol probes by
    /// mutation id first, so reaching this with a duplicate means two applies raced.</para>
    ///
    /// <para>A receipt and its subjects are one record, so they are written only inside the caller's
    /// transaction. Written apart, a failure between them would leave a receipt that cannot answer for
    /// every subject it names. A null <paramref name="transaction"/> is accepted only while the caller
    /// holds a raw <c>BEGIN</c> on the connection, as every Lexicon write does.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">No transaction is passed and none is open on the connection.</exception>
    /// <exception cref="InvalidOperationException">The catalog cannot hold evidence.</exception>
    internal static async Task InsertReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        MemoryErasureReceiptRow row,
        IReadOnlyList<byte[]> subjectDigests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(row);

        ArgumentNullException.ThrowIfNull(subjectDigests);

        if (transaction is null && !InTransaction(connection))
        {
            throw new ArgumentNullException(
                nameof(transaction),
                "A receipt and its subjects are written in one transaction: pass the caller's transaction, or hold a raw BEGIN on the connection.");
        }

        RequireStore(row.Store);

        RequireLength(row.KeyId, KeyIdBytes, nameof(row));

        RequireLength(row.RequestDigest, DigestBytes, nameof(row));

        RequireLength(row.EffectDigest, DigestBytes, nameof(row));

        foreach (byte[] subjectDigest in subjectDigests)
        {
            RequireLength(subjectDigest, DigestBytes, nameof(subjectDigests));
        }

        await RequireInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        string mutationId = Governed(row.MutationId);

        await using (SqliteCommand receipt = Command(
            connection,
            transaction,
            """
            INSERT INTO memory_erasure_receipts (
                MutationId, StoreCode, KeyId, RequestDigest, EffectDigest,
                ErasedItemCount, RemovedRowCount, RemovedLabelCount, RemovedRetirementSuppressionCount,
                AuthorshipEvidenceCode, ContextEvidenceCode, EmbeddingEvidenceCode, BackupEvidenceCode,
                OtherExternalEvidenceCode, RetainedCopiesMask, ScrubStateCode, ScrubPendingReasonMask)
            VALUES (
                $mutationId, $store, $keyId, $requestDigest, $effectDigest,
                $erasedItems, $removedRows, $removedLabels, $removedSuppressions,
                $authorship, $context, $embedding, $backup,
                $otherExternal, $retainedCopies, $scrubState, $scrubReasons);
            """))
        {
            _ = receipt.Parameters.AddWithValue("$mutationId", mutationId);

            _ = receipt.Parameters.AddWithValue("$store", (int)row.Store);

            _ = receipt.Parameters.AddWithValue("$keyId", row.KeyId);

            _ = receipt.Parameters.AddWithValue("$requestDigest", row.RequestDigest);

            _ = receipt.Parameters.AddWithValue("$effectDigest", row.EffectDigest);

            _ = receipt.Parameters.AddWithValue("$erasedItems", row.ErasedItemCount);

            _ = receipt.Parameters.AddWithValue("$removedRows", row.RemovedRowCount);

            _ = receipt.Parameters.AddWithValue("$removedLabels", row.RemovedLabelCount);

            _ = receipt.Parameters.AddWithValue("$removedSuppressions", row.RemovedRetirementSuppressionCount);

            _ = receipt.Parameters.AddWithValue("$authorship", (int)row.Authorship);

            _ = receipt.Parameters.AddWithValue("$context", (int)row.Context);

            _ = receipt.Parameters.AddWithValue("$embedding", (int)row.Embedding);

            _ = receipt.Parameters.AddWithValue("$backup", (int)row.Backup);

            _ = receipt.Parameters.AddWithValue("$otherExternal", (int)row.OtherExternal);

            _ = receipt.Parameters.AddWithValue("$retainedCopies", row.RetainedCopiesMask);

            _ = receipt.Parameters.AddWithValue("$scrubState", row.ScrubStateCode);

            _ = receipt.Parameters.AddWithValue("$scrubReasons", row.ScrubPendingReasonMask);

            _ = await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand subject = Command(
            connection,
            transaction,
            "INSERT INTO memory_erasure_receipt_subjects (MutationId, SubjectDigest) VALUES ($mutationId, $subjectDigest);");

        _ = subject.Parameters.AddWithValue("$mutationId", mutationId);

        SqliteParameter digest = subject.Parameters.Add("$subjectDigest", SqliteType.Blob);

        foreach (byte[] subjectDigest in subjectDigests)
        {
            digest.Value = subjectDigest;

            _ = await subject.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads one receipt by its mutation id.</summary>
    /// <returns>The receipt, or null when none is recorded.</returns>
    internal static async Task<MemoryErasureReceiptRow?> ReadReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid mutationId,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT MutationId, StoreCode, KeyId, RequestDigest, EffectDigest,
                   ErasedItemCount, RemovedRowCount, RemovedLabelCount, RemovedRetirementSuppressionCount,
                   AuthorshipEvidenceCode, ContextEvidenceCode, EmbeddingEvidenceCode, BackupEvidenceCode,
                   OtherExternalEvidenceCode, RetainedCopiesMask, ScrubStateCode, ScrubPendingReasonMask
            FROM memory_erasure_receipts
            WHERE MutationId = $mutationId;
            """);

        _ = command.Parameters.AddWithValue("$mutationId", Governed(mutationId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadReceiptRow(reader) : null;
    }

    /// <summary>Whether any receipt records this subject as erased.</summary>
    internal static async Task<bool> SubjectErasedAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        byte[] subjectDigest,
        CancellationToken cancellationToken)
    {
        RequireLength(subjectDigest, DigestBytes, nameof(subjectDigest));

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM memory_erasure_receipt_subjects WHERE SubjectDigest = $subjectDigest);");

        _ = command.Parameters.AddWithValue("$subjectDigest", subjectDigest);

        return await ExistsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the WAL-checkpoint reason from pending receipts, one or all, and verifies each that had no
    /// other reason.
    /// </summary>
    /// <remarks>
    /// Only receipts that still carry the WAL reason are touched, so a retry rewrites nothing it need
    /// not. The other reasons never upgrade, so a receipt that carries one stays pending for it. These
    /// are exactly the two moves the receipt table's update guard permits, and the table's own check
    /// that a pending receipt always names a reason holds on both sides of the statement.
    /// </remarks>
    /// <param name="mutationId">One receipt, or null for every pending receipt.</param>
    /// <returns>How many receipts this call moved to Verified.</returns>
    internal static async Task<long> ClearWalPendingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid? mutationId,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            UPDATE memory_erasure_receipts
            SET ScrubPendingReasonMask = ScrubPendingReasonMask & ~1,
                ScrubStateCode = CASE WHEN (ScrubPendingReasonMask & ~1) = 0 THEN 2 ELSE 1 END
            WHERE ScrubStateCode = 1
              AND (ScrubPendingReasonMask & 1) = 1
              AND ($mutationId IS NULL OR MutationId = $mutationId)
            RETURNING ScrubStateCode;
            """);

        _ = command.Parameters.AddWithValue(
            "$mutationId",
            mutationId is { } id ? Governed(id) : (object)DBNull.Value);

        long verified = 0;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetInt64(0) == ScrubVerified)
            {
                verified++;
            }
        }

        return verified;
    }

    /// <summary>The evidence tables, as the retention inventory names the store its counts come from.</summary>
    internal static string InventoryStore => "memory_erasure_fingerprints + memory_erasure_receipts + memory_erasure_receipt_subjects";

    /// <summary>
    /// How many fingerprints, receipts and receipt subjects the catalog holds, whatever key or store
    /// wrote them.
    /// </summary>
    /// <remarks>
    /// Counts only, for the retention inventory: status and the factory preview report them and never
    /// select the rows. The three counts are one statement and so one snapshot. A catalog that cannot
    /// hold evidence answers zero; past that gate the tables are counted directly, so a catalog that
    /// claims to hold evidence but lacks a table fails rather than reporting a zero it never measured.
    /// </remarks>
    internal static async Task<(long Fingerprints, long Receipts, long ReceiptSubjects)> ReadInventoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return (0, 0, 0);
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT
                (SELECT COUNT(*) FROM memory_erasure_fingerprints),
                (SELECT COUNT(*) FROM memory_erasure_receipts),
                (SELECT COUNT(*) FROM memory_erasure_receipt_subjects);
            """);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The erasure evidence inventory returned no row.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>Counts the evidence per store, split by whether <paramref name="currentKeyId"/> can verify it.</summary>
    /// <param name="currentKeyId">The current key's identifier, or null when there is no key, so nothing verifies.</param>
    internal static async Task<MemoryErasureEvidenceCounts> CountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        byte[]? currentKeyId,
        CancellationToken cancellationToken)
    {
        if (currentKeyId is not null)
        {
            RequireLength(currentKeyId, KeyIdBytes, nameof(currentKeyId));
        }

        Dictionary<MemoryReviewStore, (long Fingerprints, long Unverifiable)> fingerprints = [];

        Dictionary<MemoryReviewStore, (long Receipts, long Unverifiable)> receipts = [];

        long unverifiableReceipts = 0;

        long pendingReceipts = 0;

        if (await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            await using (SqliteCommand command = Command(
                connection,
                transaction,
                """
                SELECT StoreCode,
                       count(*),
                       coalesce(sum(CASE WHEN $keyId IS NULL OR KeyId <> $keyId THEN 1 ELSE 0 END), 0)
                FROM memory_erasure_fingerprints
                GROUP BY StoreCode;
                """))
            {
                _ = command.Parameters.AddWithValue("$keyId", (object?)currentKeyId ?? DBNull.Value);

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    fingerprints[(MemoryReviewStore)reader.GetInt32(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                }
            }

            await using SqliteCommand receiptCommand = Command(
                connection,
                transaction,
                """
                SELECT StoreCode,
                       count(*),
                       coalesce(sum(CASE WHEN $keyId IS NULL OR KeyId <> $keyId THEN 1 ELSE 0 END), 0),
                       coalesce(sum(CASE WHEN ScrubStateCode = 1 THEN 1 ELSE 0 END), 0)
                FROM memory_erasure_receipts
                GROUP BY StoreCode;
                """);

            _ = receiptCommand.Parameters.AddWithValue("$keyId", (object?)currentKeyId ?? DBNull.Value);

            await using SqliteDataReader receiptReader =
                await receiptCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await receiptReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                receipts[(MemoryReviewStore)receiptReader.GetInt32(0)] = (receiptReader.GetInt64(1), receiptReader.GetInt64(2));

                unverifiableReceipts += receiptReader.GetInt64(2);

                pendingReceipts += receiptReader.GetInt64(3);
            }
        }

        MemoryErasureStoreCountsDto[] stores =
        [
            .. Stores.Select(store =>
            {
                (long total, long unverifiable) = fingerprints.GetValueOrDefault(store);

                return new MemoryErasureStoreCountsDto(store, total, unverifiable, receipts.GetValueOrDefault(store).Receipts);
            }),
        ];

        long[] unverifiableStoreReceipts = [.. Stores.Select(store => receipts.GetValueOrDefault(store).Unverifiable)];

        return new MemoryErasureEvidenceCounts(stores, unverifiableReceipts, pendingReceipts, unverifiableStoreReceipts);
    }

    /// <summary>
    /// The receipts still pending on the write-ahead log, by mutation id: the snapshot a scrub takes
    /// before it checkpoints, so it clears only what the checkpoint can have covered.
    /// </summary>
    /// <returns>The mutation ids in their stored order, or none when the catalog cannot hold evidence.</returns>
    internal static async Task<IReadOnlyList<Guid>> ReadWalPendingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        await using SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT MutationId
            FROM memory_erasure_receipts
            WHERE ScrubStateCode = 1
              AND (ScrubPendingReasonMask & 1) = 1
            ORDER BY MutationId;
            """);

        List<Guid> pending = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            pending.Add(Guid.ParseExact(reader.GetString(0), "D"));
        }

        return pending;
    }

    /// <summary>Reads every fingerprint, receipt and receipt subject as values, each in primary-key order.</summary>
    /// <remarks>
    /// Three statements, so they are one snapshot only inside the caller's transaction: a restore reads
    /// its destination under one <c>BEGIN DEFERRED</c>, and a read outside one could see a receipt
    /// without the subjects committed beside it.
    /// </remarks>
    /// <returns>The rows, or null when the catalog cannot hold evidence.</returns>
    internal static async Task<MemoryErasureEvidenceSnapshot?> ReadSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        List<MemoryErasureFingerprintRow> fingerprints = [];

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT Fingerprint, StoreCode, KeyId FROM memory_erasure_fingerprints ORDER BY Fingerprint;"))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                fingerprints.Add(new MemoryErasureFingerprintRow(
                    (byte[])reader.GetValue(0),
                    (MemoryReviewStore)reader.GetInt32(1),
                    (byte[])reader.GetValue(2)));
            }
        }

        List<MemoryErasureReceiptRow> receipts = [];

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            """
            SELECT MutationId, StoreCode, KeyId, RequestDigest, EffectDigest,
                   ErasedItemCount, RemovedRowCount, RemovedLabelCount, RemovedRetirementSuppressionCount,
                   AuthorshipEvidenceCode, ContextEvidenceCode, EmbeddingEvidenceCode, BackupEvidenceCode,
                   OtherExternalEvidenceCode, RetainedCopiesMask, ScrubStateCode, ScrubPendingReasonMask
            FROM memory_erasure_receipts
            ORDER BY MutationId;
            """))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                receipts.Add(ReadReceiptRow(reader));
            }
        }

        List<MemoryErasureReceiptSubjectRow> subjects = [];

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            "SELECT MutationId, SubjectDigest FROM memory_erasure_receipt_subjects ORDER BY MutationId, SubjectDigest;"))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                subjects.Add(new MemoryErasureReceiptSubjectRow(
                    Guid.ParseExact(reader.GetString(0), "D"),
                    (byte[])reader.GetValue(1)));
            }
        }

        return new MemoryErasureEvidenceSnapshot(fingerprints, receipts, subjects);
    }

    /// <summary>
    /// Discards every fingerprint and receipt recorded under a key other than
    /// <paramref name="currentKeyId"/>. The discarded receipts' subjects go with them.
    /// </summary>
    internal static async Task<(long Fingerprints, long Receipts)> DeleteUnverifiableAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        byte[] currentKeyId,
        CancellationToken cancellationToken)
    {
        RequireLength(currentKeyId, KeyIdBytes, nameof(currentKeyId));

        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            return (0, 0);
        }

        long fingerprints;

        await using (SqliteCommand command = Command(
            connection,
            transaction,
            "DELETE FROM memory_erasure_fingerprints WHERE KeyId <> $keyId;"))
        {
            _ = command.Parameters.AddWithValue("$keyId", currentKeyId);

            fingerprints = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand receipts = Command(
            connection,
            transaction,
            "DELETE FROM memory_erasure_receipts WHERE KeyId <> $keyId;");

        _ = receipts.Parameters.AddWithValue("$keyId", currentKeyId);

        return (fingerprints, await receipts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Makes <paramref name="destination"/>'s evidence the catalog's whole evidence, row for row: restore
    /// staging's destination-authoritative join.
    /// </summary>
    /// <remarks>
    /// <para>Not a union. Every staged subject, receipt and fingerprint is deleted, in that order and
    /// explicitly rather than by relying on <c>PRAGMA foreign_keys</c>, and the destination's rows are then
    /// inserted verbatim, each receipt before its subjects. A union would keep an archived fingerprint this
    /// installation has since released, and so undo the release; it would also keep rows recorded under
    /// another installation's key, which this installation can never verify.</para>
    ///
    /// <para>It goes by the tables rather than the recorded Core version. A staged catalog without the
    /// fingerprint table holds no evidence, so with nothing to insert this is a no-op; with rows to insert
    /// it refuses, because the destination's evidence would be lost. A catalog that has the table is
    /// emptied whatever its metadata says, so no archived row survives to become visible later.</para>
    /// </remarks>
    /// <returns>
    /// The archive rows dropped: staged fingerprints and receipts whose primary key the destination does
    /// not hold, whether recorded under another key or released here since. Subjects are not counted.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The catalog has no evidence tables, or cannot record evidence, and the destination holds rows.
    /// </exception>
    internal static async Task<long> ReplaceAllAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryErasureEvidenceSnapshot destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        ArgumentNullException.ThrowIfNull(destination);

        bool holdsRows = destination.HasRows || destination.Subjects.Count > 0;

        await using (SqliteCommand probe = Command(
            connection,
            transaction,
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'memory_erasure_fingerprints');"))
        {
            if (!await ExistsAsync(probe, cancellationToken).ConfigureAwait(false))
            {
                return holdsRows
                    ? throw new InvalidOperationException("The catalog has no evidence tables to take the destination's erasure evidence.")
                    : 0;
            }
        }

        HashSet<string> keptFingerprints = [.. destination.Fingerprints.Select(static row => Convert.ToHexString(row.Fingerprint))];

        HashSet<string> keptReceipts = [.. destination.Receipts.Select(static row => Governed(row.MutationId))];

        long dropped = 0;

        await using (SqliteCommand fingerprints = Command(connection, transaction, "SELECT Fingerprint FROM memory_erasure_fingerprints;"))
        {
            await using SqliteDataReader reader = await fingerprints.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.GetValue(0) is not byte[] fingerprint || !keptFingerprints.Contains(Convert.ToHexString(fingerprint)))
                {
                    dropped++;
                }
            }
        }

        await using (SqliteCommand receipts = Command(connection, transaction, "SELECT MutationId FROM memory_erasure_receipts;"))
        {
            await using SqliteDataReader reader = await receipts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Compared by the identity, not the stored text: an archive is somebody else's database.
                if (reader.IsDBNull(0)
                    || !Guid.TryParse(reader.GetString(0), out Guid mutationId)
                    || !keptReceipts.Contains(Governed(mutationId)))
                {
                    dropped++;
                }
            }
        }

        foreach (string table in (string[])["memory_erasure_receipt_subjects", "memory_erasure_receipts", "memory_erasure_fingerprints"])
        {
            await using SqliteCommand delete = Command(connection, transaction, $"DELETE FROM {table};");

            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (MemoryErasureFingerprintRow row in destination.Fingerprints)
        {
            if (!await InsertFingerprintAsync(connection, transaction, row.Fingerprint, row.Store, row.KeyId, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException("The destination's evidence names one fingerprint twice.");
            }
        }

        Dictionary<Guid, List<byte[]>> subjects = [];

        foreach (MemoryErasureReceiptRow receipt in destination.Receipts)
        {
            subjects[receipt.MutationId] = [];
        }

        foreach (MemoryErasureReceiptSubjectRow subject in destination.Subjects)
        {
            if (!subjects.TryGetValue(subject.MutationId, out List<byte[]>? digests))
            {
                throw new InvalidOperationException("The destination's evidence names a receipt subject without its receipt.");
            }

            digests.Add(subject.SubjectDigest);
        }

        foreach (MemoryErasureReceiptRow receipt in destination.Receipts)
        {
            await InsertReceiptAsync(connection, transaction, receipt, subjects[receipt.MutationId], cancellationToken)
                .ConfigureAwait(false);
        }

        return dropped;
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = sql;

        return command;
    }

    /// <summary>The receipt on the reader's current row, whose columns are in <see cref="MemoryErasureReceiptRow"/>'s order.</summary>
    private static MemoryErasureReceiptRow ReadReceiptRow(SqliteDataReader reader) =>
        new(
            Guid.ParseExact(reader.GetString(0), "D"),
            (MemoryReviewStore)reader.GetInt32(1),
            (byte[])reader.GetValue(2),
            (byte[])reader.GetValue(3),
            (byte[])reader.GetValue(4),
            reader.GetInt32(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            (MemoryExternalEvidence)reader.GetInt32(9),
            (MemoryExternalEvidence)reader.GetInt32(10),
            (MemoryExternalEvidence)reader.GetInt32(11),
            (MemoryExternalEvidence)reader.GetInt32(12),
            (MemoryExternalEvidence)reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetInt32(15),
            reader.GetInt32(16));

    /// <summary>Reads an <c>EXISTS</c>, refusing anything but 0 or 1 rather than reading it as absence.</summary>
    private static async Task<bool> ExistsAsync(SqliteCommand command, CancellationToken cancellationToken) =>
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            0L => false,
            1L => true,
            _ => throw new InvalidDataException("An evidence existence check did not answer 0 or 1."),
        };

    private static async Task RequireInstalledAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (!await IsInstalledAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Erasure evidence is recorded only at Core schema version {CoreSchemaVersion} or later.");
        }
    }

    private static void RequireStore(MemoryReviewStore store)
    {
        if (store is not (MemoryReviewStore.Covenant or MemoryReviewStore.Saga or MemoryReviewStore.Lexicon))
        {
            throw new ArgumentOutOfRangeException(nameof(store), store, "Evidence belongs to a recognized store.");
        }
    }

    private static void RequireLength(byte[] value, int length, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);

        if (value.Length != length)
        {
            throw new ArgumentException($"This evidence value is exactly {length} bytes.", parameterName);
        }
    }

    /// <summary>The governed spelling of a mutation id: upper-case and dashed.</summary>
    private static string Governed(Guid mutationId) => mutationId.ToString("D").ToUpperInvariant();
}
