using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Reads what this installation's receipts say left it: the persisted buckets, plus every receipt the
/// disclosure fold has not reached yet (§10.13).
/// </summary>
/// <remarks>
/// A build without the live fold wrote receipts and never touched a bucket, so an upgraded installation
/// can hold disclosures that appear in no bucket at all. Reading the buckets alone would report that
/// nothing left when the receipts say otherwise. This read folds each unfolded tail in memory with the
/// same algebra and row rebuild the producer uses, and weakens every bucket such a tail reaches to a
/// lower bound. It writes nothing.
///
/// <para>The buckets and the tails are read in one snapshot. Read apart, a fold committing between
/// them would move receipts out of the tails the second read sees and into buckets the first read
/// already missed, and those receipts would be counted nowhere.</para>
///
/// <para>A missing <c>external_disclosure_state</c>, <c>disclosure_subject_state</c> or
/// <c>external_disclosure_receipts</c> table reads as empty: an installation that predates them has
/// nothing in them to report. A malformed persisted bucket throws <see cref="ArgumentException"/>
/// from <see cref="CovenantDisclosureState"/>, as every bucket read does.</para>
/// </remarks>
internal static class ExternalDisclosureStateReader
{
    /// <summary>
    /// Reads the effective buckets inside one <c>BEGIN DEFERRED</c> snapshot this method owns.
    /// </summary>
    /// <param name="connection">An open connection with no transaction in progress.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <param name="afterPersistedReadForTests">
    /// Runs after the persisted buckets are read and before the tails are, so a suite can commit a
    /// fold in between and prove both reads came from one snapshot.
    /// </param>
    internal static async Task<IReadOnlyList<CovenantDisclosureState>> ReadEffectiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterPersistedReadForTests = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);

        IReadOnlyList<CovenantDisclosureState> effective = await ReadWithinAsync(
            connection,
            snapshot,
            afterPersistedReadForTests,
            cancellationToken).ConfigureAwait(false);

        await snapshot.RollbackAsync(cancellationToken).ConfigureAwait(false);

        return effective;
    }

    /// <summary>
    /// Reads the effective buckets inside a snapshot the caller already holds.
    /// </summary>
    internal static Task<IReadOnlyList<CovenantDisclosureState>> ReadEffectiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        ArgumentNullException.ThrowIfNull(transaction);

        return ReadWithinAsync(connection, transaction, null, cancellationToken);
    }

    private static async Task<IReadOnlyList<CovenantDisclosureState>> ReadWithinAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Func<CancellationToken, Task>? afterPersistedReadForTests,
        CancellationToken cancellationToken)
    {
        HashSet<string> tables = await ReadPresentTablesAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<int, CovenantDisclosureState> buckets = [];

        if (tables.Contains("external_disclosure_state"))
        {
            foreach (CovenantDisclosureState persisted in await ExternalDisclosureStateStore
                         .ReadAllAsync(connection, transaction, cancellationToken)
                         .ConfigureAwait(false))
            {
                buckets[BucketKey(persisted.Destination, persisted.Revocability)] = persisted;
            }
        }

        if (afterPersistedReadForTests is not null)
        {
            await afterPersistedReadForTests(cancellationToken).ConfigureAwait(false);
        }

        HashSet<int> reached = [];

        if (tables.Contains("disclosure_subject_state") && tables.Contains("external_disclosure_receipts"))
        {
            foreach (ExternalDisclosureUnfoldedSubject subject in await ExternalDisclosureStateFold
                         .ReadUnfoldedSubjectsAsync(connection, transaction, cancellationToken)
                         .ConfigureAwait(false))
            {
                foreach (ExternalDisclosureFoldableReceipt receipt in await ExternalDisclosureStateFold
                             .ReadTailAsync(
                                 connection,
                                 transaction,
                                 subject.Key,
                                 subject.LastFoldedOrdinal,
                                 cancellationToken)
                             .ConfigureAwait(false))
                {
                    int bucket = BucketKey(receipt.Destination, receipt.Revocability);

                    buckets[bucket] = receipt.FoldInto(
                        buckets.TryGetValue(bucket, out CovenantDisclosureState? state)
                            ? state
                            : CovenantDisclosureState.Empty(receipt.Destination, receipt.Revocability));

                    _ = reached.Add(bucket);
                }
            }
        }

        List<CovenantDisclosureState> effective = new(buckets.Count);

        // In destination, then revocability order, as the persisted read returns them.
        for (int bucket = 0; bucket <= BucketKey(CovenantEgressDestination.EncryptedBackup, CovenantDisclosureRevocability.Nonrevocable); bucket++)
        {
            if (buckets.TryGetValue(bucket, out CovenantDisclosureState? state))
            {
                // An unfolded receipt is evidence of a disclosure, and nothing proves its tail is the
                // whole of that subject's history, so every bucket one reaches can only claim a lower
                // bound.
                effective.Add(reached.Contains(bucket)
                    ? CovenantDisclosureStateAlgebra.WeakenToLowerBound(state)
                    : state);
            }
        }

        return effective;
    }

    /// <summary>
    /// One integer per bucket, ordered by destination and then revocability; the codes are closed
    /// small ranges, so the key is exact.
    /// </summary>
    private static int BucketKey(
        CovenantEgressDestination destination,
        CovenantDisclosureRevocability revocability) =>
        ((int)destination * 4) + (int)revocability;

    private static async Task<HashSet<string>> ReadPresentTablesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        HashSet<string> tables = new(StringComparer.Ordinal);

        await using SqliteCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
                AND name IN ('external_disclosure_state', 'disclosure_subject_state', 'external_disclosure_receipts');
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            _ = tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
