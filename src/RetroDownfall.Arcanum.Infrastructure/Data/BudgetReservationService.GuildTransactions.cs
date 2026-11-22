using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class BudgetReservationService
{
    /// <summary>Current policy, including a gate enabled after physical preparation.</summary>
    private static readonly Error UnavailableGuildReservation = new(ErrorCodes.Guild.ReconciliationRequired,
        "The exact owned daily reservation is unavailable for this accounting action.");

    /// <summary>Preserves the exact legacy owner's possible dispatch without applying a spend ceiling.</summary>
    internal async Task<Result> ExposeHistoricalReservationWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned?.Status != BudgetReservationStatus.Reserved)
            return UnavailableGuildReservation;

        return await SetDispositionWithinAsync(connection, transaction, owned, BudgetReservationStatus.UncertainPaid,
            owned.ReconciledUsd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies cumulative measured usage without settling unknown work or inventing another bill.</summary>
    internal async Task<Result> RecordPartialUsageWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, decimal knownCostUsd, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(knownCostUsd);

        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned?.Status != BudgetReservationStatus.UncertainPaid || knownCostUsd < owned.ReconciledUsd)
            return UnavailableGuildReservation;

        return await SetDispositionWithinAsync(connection, transaction, owned, BudgetReservationStatus.UncertainPaid,
            knownCostUsd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks the exact existing physical reservation without mutating it.</summary>
    internal async Task<Result> ValidateCorrectionWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, decimal knownCostUsd, bool completeKnown, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned is null || knownCostUsd < owned.ReconciledUsd) return UnavailableGuildReservation;

        return owned.Status == BudgetReservationStatus.UncertainPaid || owned.Status == BudgetReservationStatus.Reconciled && completeKnown
            ? Result.Success() : UnavailableGuildReservation;
    }

    /// <summary>Adjusts an existing physical reservation without changing an uncertain disposition.</summary>
    internal async Task<Result> CorrectUsageWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, decimal knownCostUsd, bool completeKnown, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned is null || knownCostUsd < owned.ReconciledUsd) return UnavailableGuildReservation;

        return owned.Status switch
        {
            BudgetReservationStatus.UncertainPaid => await RecordPartialUsageWithinAsync(connection, transaction, reservationId, runId, knownCostUsd, cancellationToken).ConfigureAwait(false),
            BudgetReservationStatus.Reconciled when completeKnown => await ReconcileWithinAsync(connection, transaction, reservationId, runId, knownCostUsd, true, cancellationToken).ConfigureAwait(false),
            _ => UnavailableGuildReservation,
        };
    }

    /// <summary>Only the trusted accounting owner with complete measured evidence may reconcile an uncertain hold.</summary>
    internal async Task<Result> ReconcileWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, decimal actualCostUsd, bool completeUsageProven,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actualCostUsd);

        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (!completeUsageProven || owned is null || actualCostUsd < owned.ReconciledUsd) return UnavailableGuildReservation;

        if (owned.Status == BudgetReservationStatus.Reconciled && owned.ReconciledUsd == actualCostUsd)
            return Result.Success();

        if (owned.Status is not (BudgetReservationStatus.Reserved or BudgetReservationStatus.UncertainPaid or BudgetReservationStatus.Reconciled))
            return UnavailableGuildReservation;

        return await SetDispositionWithinAsync(connection, transaction, owned, BudgetReservationStatus.Reconciled,
            actualCostUsd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Proven unsent work can release an owned Reserved row; uncertainty never becomes unused authority.</summary>
    internal async Task<Result> ReleaseWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned?.Status == BudgetReservationStatus.Released) return Result.Success();

        if (owned?.Status != BudgetReservationStatus.Reserved) return UnavailableGuildReservation;

        return await SetDispositionWithinAsync(connection, transaction, owned, BudgetReservationStatus.Released,
            0m, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A trusted no-dispatch receipt may resolve the marker, but cannot contradict already measured usage.</summary>
    internal async Task<Result> ReleaseProvenNoDispatchWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned is null || owned.ReconciledUsd != 0m) return UnavailableGuildReservation;

        if (owned.Status == BudgetReservationStatus.Reserved || owned.Status == BudgetReservationStatus.Released)
            return await ReleaseWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned.Status is not (BudgetReservationStatus.UncertainPaid or BudgetReservationStatus.Reconciled)) return UnavailableGuildReservation;

        return await ReconcileWithinAsync(connection, transaction, reservationId, runId, 0m, completeUsageProven: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<BudgetReservation?> ReadOwnedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, CancellationToken cancellationToken)
    {
        RequireTransaction(connection, transaction);

        await using (DbCommand census = connection.CreateCommand())
        {
            census.Transaction = transaction;

            census.CommandText = "SELECT COUNT(*) FROM BudgetReservations WHERE lower(replace(Id,'-',''))=@id";

            AddParameter(census, "@id", reservationId.ToString("N"));

            if (!Equals(1L, await census.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))) return null;
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            SELECT BudgetPeriod,ReservedUsd,ReconciledUsd,Status,ExpiresAt,CreatedAt FROM BudgetReservations
            WHERE Id=@id AND RunId=@run
            """;

        AddParameter(command, "@id", reservationId.ToString("N"));

        AddParameter(command, "@run", runId.ToString("N"));

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reservationId, runId, reader.GetString(0), ExactUsdText.Read(reader, 1), ExactUsdText.Read(reader, 2),
                (BudgetReservationStatus)reader.GetInt32(3), UtcInstantText.Parse(reader.GetString(4)), UtcInstantText.Parse(reader.GetString(5)))
            : null;
    }

    private static async Task<Result> SetDispositionWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        BudgetReservation owned, BudgetReservationStatus status, decimal reconciledUsd, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE BudgetReservations SET Status=@status,ReconciledUsd=@reconciled,UpdatedAt=@now
            WHERE Id=@id AND RunId=@run AND Status=@previous
            """;

        AddParameter(command, "@status", (int)status);

        ExactUsdText.AddParameter(command, "@reconciled", reconciledUsd);

        AddParameter(command, "@now", UtcInstantText.Format(DateTimeOffset.UtcNow));

        AddParameter(command, "@id", owned.Id.ToString("N"));

        AddParameter(command, "@run", owned.RunId.ToString("N"));

        AddParameter(command, "@previous", (int)owned.Status);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 ? Result.Success() : UnavailableGuildReservation;
    }

    private static void RequireTransaction(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("The daily accounting primitive requires its admitted connection and active transaction.", nameof(transaction));
    }
}
