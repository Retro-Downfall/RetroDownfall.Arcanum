using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// Folds the effective, nonrevocable disclosure state into one possible-attempt count, on a
/// caller-owned connection.
/// </summary>
/// <remarks>
/// The count is the effective one (§10.13): every persisted nonrevocable bucket, plus every
/// nonrevocable receipt in a subject tail the fold has not reached. Receipts written before the live
/// fold existed are therefore counted, and because nothing proves such a tail is complete, any one of
/// them makes the total a lower bound. That is exactly the nonrevocable sum of
/// <see cref="ExternalDisclosureStateReader"/>'s buckets.
///
/// <para>Only the total and its kind are needed here, so the receipts are counted rather than
/// rebuilt: rebuilding recomputes every receipt digest, which a status read would pay for each
/// unfolded receipt on every call. Both parts are one statement, so they come from one snapshot whether
/// or not the caller holds a transaction, and a fold committing beside this read can neither drop a
/// receipt nor count one twice.</para>
/// </remarks>
internal sealed class CovenantDisclosureExposureReader
{

    private static readonly Error MalformedExposure = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "The nonrevocable Covenant disclosure exposure could not be folded safely.");

    /// <summary>The persisted nonrevocable buckets, tagged as the first part of the read.</summary>
    private const string PersistedPart = """
        SELECT 1, DestinationCode, CountKindCode, JoinedCount
        FROM external_disclosure_state
        WHERE RevocabilityCode = 2
        """;

    /// <summary>
    /// The unfolded nonrevocable receipts per destination, tagged as the second part. The subject
    /// predicate is <c>idx_disclosure_subject_state_unfolded</c>'s own, so the partial index serves it.
    /// </summary>
    private const string UnfoldedPart = """
        SELECT 2, receipt.DestinationCode, 2, COUNT(*)
        FROM disclosure_subject_state AS subject
        JOIN external_disclosure_receipts AS receipt
            ON receipt.OriginInstallationId = subject.OriginInstallationId
            AND receipt.SubjectKind = subject.SubjectKind
            AND receipt.SubjectId = subject.SubjectId
            AND receipt.SubjectOrdinal > subject.LastFoldedOrdinal
        WHERE subject.LastFoldedOrdinal < subject.LastAllocatedOrdinal
            AND receipt.RevocabilityCode = 2
        GROUP BY receipt.DestinationCode
        """;

    /// <param name="callerOwnedConnection">The connection to read on.</param>
    /// <param name="transaction">The caller's snapshot, when it holds one.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    internal async Task<Result<CovenantDisclosureExposure>> ReadWithinAsync(
        SqliteConnection callerOwnedConnection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(callerOwnedConnection);

        try
        {

            bool persisted = false;

            int receiptTables = 0;

            await using (SqliteCommand probe = callerOwnedConnection.CreateCommand())
            {

                probe.Transaction = transaction;

                probe.CommandText = """
                    SELECT name
                    FROM sqlite_master
                    WHERE type = 'table'
                        AND name IN (
                            'external_disclosure_state',
                            'disclosure_subject_state',
                            'external_disclosure_receipts');
                    """;

                await using SqliteDataReader tables = await probe
                    .ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await tables.ReadAsync(cancellationToken).ConfigureAwait(false))
                {

                    if (tables.GetString(0) == "external_disclosure_state")
                    {

                        persisted = true;

                    }
                    else
                    {

                        receiptTables++;

                    }

                }

            }

            // A missing table is absence, not failure: an installation that predates it has nothing
            // in it to report.
            string? sql = !persisted
                ? receiptTables == 2 ? UnfoldedPart + "\nORDER BY 2;" : null
                : receiptTables == 2
                    ? PersistedPart + "\nUNION ALL\n" + UnfoldedPart + "\nORDER BY 1, 2;"
                    : PersistedPart + "\nORDER BY 2;";

            long attempts = 0;

            CovenantDisclosureCountKind joinedKind = CovenantDisclosureCountKind.Exact;

            if (sql is null)
            {

                return Result<CovenantDisclosureExposure>.Success(
                    new CovenantDisclosureExposure(attempts, joinedKind));

            }

            await using SqliteCommand command = callerOwnedConnection.CreateCommand();

            command.Transaction = transaction;

            // SUM is deliberately absent. SQLite raises integer overflow before C# can map it to the
            // one content-free integrity error, while at most sixteen literal rows can be checked here.
            command.CommandText = sql;

            HashSet<long> persistedDestinations = [];

            HashSet<long> unfoldedDestinations = [];

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                if (reader.GetValue(0) is not long part
                    || part is not (1 or 2)
                    || reader.GetValue(1) is not long destination
                    || destination is < 1 or > 8
                    || !(part == 1 ? persistedDestinations : unfoldedDestinations).Add(destination)
                    || reader.GetValue(2) is not long countKindCode
                    || countKindCode is not (long)CovenantDisclosureCountKind.Exact
                        and not (long)CovenantDisclosureCountKind.LowerBound
                    || reader.GetValue(3) is not long count
                    || count < 0)
                {

                    return Result<CovenantDisclosureExposure>.Failure(MalformedExposure);

                }

                attempts = checked(attempts + count);

                // An unfolded receipt reads as a lower bound, exactly as the effective bucket it
                // would reach does.
                if (countKindCode == (long)CovenantDisclosureCountKind.LowerBound)
                {

                    joinedKind = CovenantDisclosureCountKind.LowerBound;

                }

            }

            return Result<CovenantDisclosureExposure>.Success(
                new CovenantDisclosureExposure(attempts, joinedKind));

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {

            throw;

        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidCastException or OverflowException or ArgumentException)
        {

            return Result<CovenantDisclosureExposure>.Failure(MalformedExposure);

        }

    }

}
