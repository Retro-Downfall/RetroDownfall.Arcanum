using System.Globalization;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Backup;
using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Backup;

/// <summary>
/// What an archive carries in the way of protected state, read before anything is staged.
/// </summary>
/// <remarks>
/// This runs against the <em>extracted</em> snapshot in the restore's work directory, opened read-only,
/// before the staged tree is composed and before any exclusive owner exists. That ordering is the whole
/// point: the default mode has to be able to refuse an archive that carries Covenant memory without the
/// refusal having first closed admission, published a journal, or laid down a staged generation
/// (§10.19.10).
///
/// <para>Nothing here writes, and nothing here reads content. The counts are <c>COUNT(*)</c> over the
/// canonical family and the label ledger, and the taint bit is one status code — so the whole inventory
/// survives being carried in a refusal message and a durable phase record.</para>
/// </remarks>
internal static class BackupRestoreProtectedStateInspector
{

    /// <summary>
    /// The canonical family's content tables, which is every canonical object except the singleton.
    /// </summary>
    /// <remarks>
    /// <c>covenant_state</c> is deliberately absent. The schema installer seeds it on every
    /// Covenant-enabled installation, so counting it would make an archive from an installation that
    /// merely has the tier installed read as carrying protected state — and the default would then
    /// refuse a restore that has nothing to refuse.
    ///
    /// <para>The order is a **delete-safe topological order**, not an alphabetical one, because
    /// <see cref="BackupRestoreProtectedStatePurger"/> empties these tables in exactly this sequence and
    /// foreign keys are enforced. <c>covenant_heads</c> references both <c>covenant_versions</c> and
    /// <c>covenant_entries</c>, provenance references versions, and versions reference entries, so every
    /// child precedes its parent. <c>covenant_key_epochs</c> comes last of the independent tables for a
    /// second reason: deleting a head fires <c>covenant_heads_key_epoch_delete</c>, which writes an epoch
    /// row, so clearing epochs any earlier would leave the rows that trigger then created.</para>
    /// </remarks>
    internal static readonly string[] CanonicalContentTables =
    [
        "covenant_search_outbox",
        "covenant_curation_receipts",
        "covenant_curation_heads",
        "covenant_curation_versions",
        "covenant_heads",
        "covenant_version_attachment_provenance",
        "covenant_versions",
        "covenant_entries",
        "covenant_mutation_receipts",
        "covenant_turn_receipts",
        "covenant_turn_receipt_aggregate",
        "covenant_key_epochs",
    ];

    /// <summary>The accelerator's own projection of the canonical rows above.</summary>
    internal static readonly string[] AcceleratorContentTables = ["covenant_search_documents"];

    /// <summary>
    /// Inventories the Grimoire snapshot the archive extracted, or reports nothing when it carries none.
    /// </summary>
    /// <remarks>
    /// An archive with no Grimoire snapshot, or one whose portable recovery material cannot be read,
    /// contributes <see cref="BackupRestoreProtectedStateInventory.None"/> rather than a refusal. The
    /// restore already refuses both of those states with their own typed blockers, and inventing a
    /// second refusal here would report a protected-state problem for an archive whose real defect is
    /// that it cannot be opened at all.
    /// </remarks>
    internal static async Task<BackupRestoreProtectedStateInventory> InspectExtractedArchiveAsync(
        string extractRoot,
        CancellationToken cancellationToken)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(extractRoot);

        string database = Path.Combine(
            extractRoot,
            BackupArchivePaths.GrimoireDatabase.Replace('/', Path.DirectorySeparatorChar));

        string recovery = Path.Combine(
            extractRoot,
            BackupArchivePaths.PortableRecoveryKeys.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(database)
            || !BackupPortableRecoveryReader.TryReadGrimoireSecret(recovery, out string? secret))
        {

            return BackupRestoreProtectedStateInventory.None;

        }

        try
        {

            await using SqliteConnection connection = await BackupRestoreDatabaseWorker
                .OpenAsync(database, secret, readOnly: true, cancellationToken)
                .ConfigureAwait(false);

            return await InspectAsync(connection, cancellationToken).ConfigureAwait(false);

        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException
                or System.Security.Cryptography.CryptographicException)
        {

            return BackupRestoreProtectedStateInventory.None;

        }

    }

    /// <summary>
    /// Counts the canonical family, the sensitivity labels, and reads the source's authority state.
    /// </summary>
    /// <remarks>
    /// A missing table is zero rather than a failure. A snapshot taken by a build that predates the
    /// Covenant tables genuinely carries nothing, and refusing it would make an installation unable to
    /// restore an archive it is entitled to.
    /// </remarks>
    internal static async Task<BackupRestoreProtectedStateInventory> InspectAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(connection);

        long canonical = await SumAsync(connection, CanonicalContentTables, cancellationToken)
            .ConfigureAwait(false);

        long accelerator = await SumAsync(connection, AcceleratorContentTables, cancellationToken)
            .ConfigureAwait(false);

        long labels = await CountAsync(connection, "artifact_sensitivity", cancellationToken)
            .ConfigureAwait(false);

        return new BackupRestoreProtectedStateInventory(
            canonical,
            labels,
            await ReadSourceTaintAsync(connection, cancellationToken).ConfigureAwait(false),
            accelerator);

    }

    /// <summary>
    /// Folds the destination's nonrevocable disclosure buckets into one possible-attempt count.
    /// </summary>
    /// <remarks>
    /// Only <see cref="CovenantDisclosureRevocability.Nonrevocable"/> buckets are folded, because the
    /// shared destructive-operation copy is a statement about what local removal <em>cannot</em> revoke.
    /// Including a locally revocable effect would inflate the number an operator is being asked to weigh
    /// with exactly the disclosures Arcanum can still undo.
    ///
    /// <para>The count kind is the join of the buckets, not the maximum of their counts: one lower-bound
    /// bucket makes the total a lower bound, because a total that presented a joined count as exact
    /// would understate exposure in the one report an operator uses to decide (§10.15).</para>
    /// </remarks>
    internal static BackupRestoreDisclosureExposure Exposure(
        IReadOnlyList<CovenantDisclosureState> buckets)
    {

        ArgumentNullException.ThrowIfNull(buckets);

        bool everOccurred = false;

        long attempts = 0;

        CovenantDisclosureCountKind kind = CovenantDisclosureCountKind.Exact;

        foreach (CovenantDisclosureState bucket in buckets)
        {

            if (bucket is null or { Revocability: not CovenantDisclosureRevocability.Nonrevocable })
            {

                continue;

            }

            everOccurred |= bucket.EverOccurred;

            attempts = checked(attempts + checked((long)bucket.Count));

            if (bucket.CountKind is CovenantDisclosureCountKind.LowerBound)
            {

                kind = CovenantDisclosureCountKind.LowerBound;

            }

        }

        return new BackupRestoreDisclosureExposure(everOccurred, attempts, kind);

    }

    /// <summary>
    /// Whether the machine that produced this snapshot could still prove a clean authority state.
    /// </summary>
    /// <remarks>
    /// <c>PendingHostToolsTaint</c> counts as tainted. A pending transition is an installation that
    /// cannot currently prove it was never exposed to unsandboxed host tools, and "cannot prove clean"
    /// is the only reading a fail-closed decision may take from it.
    /// </remarks>
    private static async Task<bool> ReadSourceTaintAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(connection, "covenant_authority_state", cancellationToken)
                .ConfigureAwait(false))
        {

            return false;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT HostToolsStateCode FROM covenant_authority_state WHERE StateKey = 1;";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is not (null or DBNull)
            && Convert.ToInt64(value, CultureInfo.InvariantCulture)
                != (long)CovenantHostToolsState.Clean;

    }

    private static async Task<long> SumAsync(
        SqliteConnection connection,
        IEnumerable<string> tables,
        CancellationToken cancellationToken)
    {

        long total = 0;

        foreach (string table in tables)
        {

            total = checked(
                total + await CountAsync(connection, table, cancellationToken).ConfigureAwait(false));

        }

        return total;

    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {

        if (!await BackupRestoreDatabaseWorker
                .TableExistsAsync(connection, table, cancellationToken)
                .ConfigureAwait(false))
        {

            return 0;

        }

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT COUNT(*) FROM {table};";

        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull
            ? 0
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    }

}
