using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>Folds the preserved, nonrevocable disclosure buckets on a caller-owned snapshot.</summary>
internal sealed class CovenantDisclosureExposureReader
{

    private static readonly Error MalformedExposure = new(
        ErrorCodes.Covenant.IntegrityFailure,
        "The nonrevocable Covenant disclosure exposure could not be folded safely.");

    internal async Task<Result<CovenantDisclosureExposure>> ReadWithinAsync(
        SqliteConnection callerOwnedConnection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {

        ArgumentNullException.ThrowIfNull(callerOwnedConnection);

        try
        {

            await using SqliteCommand command = callerOwnedConnection.CreateCommand();

            command.Transaction = transaction;

            // SUM is deliberately absent. SQLite raises integer overflow before C# can map it to the
            // one content-free integrity error, while eight literal rows can be checked safely here.
            command.CommandText = """
                SELECT DestinationCode, CountKindCode, JoinedCount
                FROM external_disclosure_state
                WHERE RevocabilityCode = 2
                ORDER BY DestinationCode;
                """;

            long attempts = 0;

            CovenantDisclosureCountKind joinedKind = CovenantDisclosureCountKind.Exact;

            HashSet<long> destinations = [];

            await using SqliteDataReader reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {

                if (reader.GetValue(0) is not long destination
                    || destination is < 1 or > 8
                    || !destinations.Add(destination)
                    || reader.GetValue(1) is not long countKindCode
                    || countKindCode is not (long)CovenantDisclosureCountKind.Exact
                        and not (long)CovenantDisclosureCountKind.LowerBound
                    || reader.GetValue(2) is not long count
                    || count < 0)
                {

                    return Result<CovenantDisclosureExposure>.Failure(MalformedExposure);

                }

                attempts = checked(attempts + count);

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
