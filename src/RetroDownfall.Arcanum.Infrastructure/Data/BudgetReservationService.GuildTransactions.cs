using System.Data.Common;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

internal sealed partial class BudgetReservationService
{
    /// <summary>Current policy, including a gate enabled after physical preparation.</summary>
    internal bool RequiresReservation
    {
        get
        {
            BudgetSettings budget = settings.CurrentValue.ResolveBudget();

            return budget.Enabled && budget.DailyLimitUsd > 0;
        }
    }

    private static readonly Error UnavailableGuildReservation = new(ErrorCodes.Guild.ReconciliationRequired,
        "The exact owned daily reservation is unavailable for this accounting action.");

    /// <summary>
    /// The admitted immediate transaction owns both the daily reservation and its Guild association.
    /// The caller commits or rolls back; no nested transaction, retry or synthetic foreign link is created.
    /// </summary>
    internal async Task<Result<BudgetReservation?>> ReserveWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        BudgetReservationRequest request, CancellationToken cancellationToken = default)
    {
        RequireTransaction(connection, transaction);

        ArgumentOutOfRangeException.ThrowIfNegative(request.ReservedUsd);

        cancellationToken.ThrowIfCancellationRequested();

        BudgetSettings budget = settings.CurrentValue.ResolveBudget();

        if (!budget.Enabled || budget.DailyLimitUsd <= 0) return Result<BudgetReservation?>.Success(null);

        decimal limit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

        decimal current = ExactUsdText.CheckedAdd(
            await SumCommittedAsync(connection, transaction, request.BudgetPeriod, cancellationToken).ConfigureAwait(false),
            await SumOutstandingAsync(connection, transaction, request.BudgetPeriod, cancellationToken).ConfigureAwait(false));

        decimal projected = ExactUsdText.CheckedAdd(current, request.ReservedUsd);

        if (projected > limit)
            return Result<BudgetReservation?>.Failure(DailyLimitRecheckFailure(limit, current, request.ReservedUsd, request.BudgetPeriod, projected).Error);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        BudgetReservation reservation = new(Guid.NewGuid(), request.RunId, request.BudgetPeriod, request.ReservedUsd,
            0m, BudgetReservationStatus.Reserved, request.ExpiresAt, now);

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO BudgetReservations(Id,RunId,BudgetPeriod,ReservedUsd,ReconciledUsd,Status,ExpiresAt,CreatedAt,UpdatedAt)
            VALUES(@id,@run,@period,@reserved,@zero,@status,@expires,@now,@now)
            """;

        AddParameter(command, "@id", reservation.Id.ToString("N"));

        AddParameter(command, "@run", request.RunId.ToString("N"));

        AddParameter(command, "@period", request.BudgetPeriod);

        ExactUsdText.AddParameter(command, "@reserved", request.ReservedUsd);

        ExactUsdText.AddParameter(command, "@zero", 0m);

        AddParameter(command, "@status", (int)BudgetReservationStatus.Reserved);

        AddParameter(command, "@expires", UtcInstantText.Format(request.ExpiresAt));

        AddParameter(command, "@now", UtcInstantText.Format(now));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return Result<BudgetReservation?>.Success(reservation);
    }

    /// <summary>Strict owned pre-call recheck, raise and renewal on the caller's admitted transaction.</summary>
    internal async Task<Result> PrepareWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, decimal reservedUsd, decimal delegatedSpendUsd, DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned is not { Status: BudgetReservationStatus.Reserved }) return UnavailableGuildReservation;

        BudgetSettings budget = settings.CurrentValue.ResolveBudget();

        decimal target = budget.Enabled && budget.DailyLimitUsd > 0
            ? Math.Max(owned.ReservedUsd, Math.Max(0m, reservedUsd)) : owned.ReservedUsd;

        if (budget.Enabled && budget.DailyLimitUsd > 0)
        {
            decimal limit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);

            string today = TodayBudgetPeriod();

            decimal delegated = Math.Max(0m, delegatedSpendUsd);

            decimal current = ExactUsdText.CheckedAdd(
                await SumCommittedAsync(connection, transaction, owned.BudgetPeriod, cancellationToken).ConfigureAwait(false),
                await SumOutstandingAsync(connection, transaction, owned.BudgetPeriod, cancellationToken).ConfigureAwait(false));

            decimal projected = ExactUsdText.CheckedAdd(checked(current - owned.ReservedUsd), target);

            bool sameDay = string.Equals(today, owned.BudgetPeriod, StringComparison.Ordinal);

            if (sameDay) projected = ExactUsdText.CheckedAdd(projected, delegated);

            if (projected > limit)
                return DailyLimitRecheckFailure(limit, current, checked(target - owned.ReservedUsd + (sameDay ? delegated : 0m)), owned.BudgetPeriod, projected);

            if (!sameDay)
            {
                decimal todayCurrent = ExactUsdText.CheckedAdd(
                    await SumCommittedAsync(connection, transaction, today, cancellationToken).ConfigureAwait(false),
                    await SumOutstandingAsync(connection, transaction, today, cancellationToken).ConfigureAwait(false));

                decimal todayProjected = ExactUsdText.CheckedAdd(ExactUsdText.CheckedAdd(todayCurrent, target), delegated);

                if (todayProjected > limit)
                    return DailyLimitRecheckFailure(limit, todayCurrent, ExactUsdText.CheckedAdd(target, delegated), today, todayProjected);
            }
        }

        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = """
            UPDATE BudgetReservations SET ReservedUsd=@reserved, ExpiresAt=@expires, UpdatedAt=@now
            WHERE Id=@id AND RunId=@run AND Status=@status
            """;

        ExactUsdText.AddParameter(command, "@reserved", target);

        AddParameter(command, "@expires", UtcInstantText.Format(expiresAt > owned.ExpiresAt ? expiresAt : owned.ExpiresAt));

        AddParameter(command, "@now", UtcInstantText.Format(DateTimeOffset.UtcNow));

        AddParameter(command, "@id", reservationId.ToString("N"));

        AddParameter(command, "@run", runId.ToString("N"));

        AddParameter(command, "@status", (int)BudgetReservationStatus.Reserved);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 ? Result.Success() : UnavailableGuildReservation;
    }

    internal async Task<Result> MarkPossiblyDispatchedWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned is null || owned.Status is not (BudgetReservationStatus.Reserved or BudgetReservationStatus.UncertainPaid))
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

    /// <summary>Rechecks the current UTC liability and renews before the exact physical effect becomes uncertain.</summary>
    internal async Task<Result> RecheckBeforeDispatchWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        BudgetReservation? owned = await ReadOwnedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);

        if (owned?.Status != BudgetReservationStatus.Reserved) return UnavailableGuildReservation;

        DateTimeOffset now = UtcNowForTesting?.Invoke() ?? DateTimeOffset.UtcNow;

        Result admitted = await PrepareWithinAsync(connection, transaction, reservationId, runId, owned.ReservedUsd,
            0m, now.AddHours(1), cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure) return admitted;

        return await MarkPossiblyDispatchedWithinAsync(connection, transaction, reservationId, runId, cancellationToken).ConfigureAwait(false);
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
