using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// Atomic budget reservations over an exact daily ledger snapshot in <c>BEGIN IMMEDIATE</c> transactions.
/// </summary>
internal sealed class BudgetReservationService(
    ArcanumDbContext db,
    IOptionsMonitor<ArcanumSettings> settings) : IBudgetReservationService
{
    /// <summary>
    /// Runs inside <see cref="ReserveAsync"/>'s write transaction after both spend sums were read and before the
    /// reservation row is inserted, so a test can hold one reservation open at its decision point and prove a
    /// second connection cannot decide on the same ledger concurrently.
    /// </summary>
    internal Func<CancellationToken, Task>? AfterSumsBeforeReserveInsertForTesting { get; set; }

    /// <summary>
    /// Replaces the system clock wherever the service decides which UTC day is today, so a test can
    /// stand a reservation admitted on one day against spend completed on the next.
    /// </summary>
    internal Func<DateTimeOffset>? UtcNowForTesting { get; set; }

    /// <summary>
    /// Runs inside ReserveAsync and AdjustAsync after optional committed read-ahead and immediately before the write
    /// transaction begins, so a test that proves a second connection waits for the first can start its waiting
    /// window at the moment the second one is about to ask for the lock, not at the moment its task was queued.
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeWriteTransactionForTesting { get; set; }

    public async Task<Result<BudgetReservation>> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default)
    {
        // A negative reservation would lower the outstanding sum every later check reads, so it is refused
        // before anything is read or written (AdjustAsync clamps its target at zero for the same reason).
        ArgumentOutOfRangeException.ThrowIfNegative(request.ReservedUsd);

        BudgetSettings budget = settings.CurrentValue.ResolveBudget();

        if (!budget.Enabled || budget.DailyLimitUsd <= 0)
        {
            // Reservations are a no-op when budget enforcement is off — return a synthetic released record.
            return Result<BudgetReservation>.Success(new BudgetReservation(
                Id: Guid.NewGuid(),
                RunId: request.RunId,
                BudgetPeriod: request.BudgetPeriod,
                ReservedUsd: 0m,
                ReconciledUsd: 0m,
                Status: BudgetReservationStatus.Released,
                ExpiresAt: request.ExpiresAt,
                CreatedAt: DateTimeOffset.UtcNow));
        }

        decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);
        Guid id = Guid.NewGuid();
        DateTimeOffset createdAt = DateTimeOffset.UtcNow;

        try
        {
            BudgetReservation reservation = await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    using CommittedReadAhead? committedHint = await ReadCommittedAheadAsync(
                        connection, request.BudgetPeriod, cancellationToken).ConfigureAwait(false);

                    if (BeforeWriteTransactionForTesting is not null)
                    {
                        await BeforeWriteTransactionForTesting(cancellationToken).ConfigureAwait(false);
                    }

                    // BeginTransaction(deferred: false) is BEGIN IMMEDIATE with a disposal-time rollback, so an
                    // already-cancelled token can never strand an open write transaction on the scoped connection.
                    await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                    decimal committed = await ReadCommittedForDecisionAsync(connection, transaction, request.BudgetPeriod, committedHint, cancellationToken)
                        .ConfigureAwait(false);

                    decimal outstanding = await SumOutstandingAsync(connection, transaction, request.BudgetPeriod, cancellationToken)
                        .ConfigureAwait(false);

                    decimal committedAndOutstanding = ExactUsdText.CheckedAdd(committed, outstanding);

                    decimal projected = ExactUsdText.CheckedAdd(committedAndOutstanding, request.ReservedUsd);

                    if (projected > dailyLimit)
                    {
                        throw new BudgetExceededException(dailyLimit, committedAndOutstanding, request.ReservedUsd, request.BudgetPeriod, "requested", projected);
                    }

                    if (AfterSumsBeforeReserveInsertForTesting is not null)
                    {
                        await AfterSumsBeforeReserveInsertForTesting(cancellationToken).ConfigureAwait(false);
                    }

                    await using DbCommand cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText =
                        """
                        INSERT INTO "BudgetReservations"
                            ("Id", "RunId", "BudgetPeriod", "ReservedUsd", "ReconciledUsd", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt")
                        VALUES
                            (@id, @runId, @period, @reserved, @reconciled, @status, @expires, @created, @updated)
                        """;

                    AddParameter(cmd, "@id", id.ToString("N"));
                    AddParameter(cmd, "@runId", request.RunId.ToString("N"));
                    AddParameter(cmd, "@period", request.BudgetPeriod);
                    _ = ExactUsdText.AddParameter(cmd, "@reserved", request.ReservedUsd);
                    _ = ExactUsdText.AddParameter(cmd, "@reconciled", 0m);
                    AddParameter(cmd, "@status", (int)BudgetReservationStatus.Reserved);
                    AddParameter(cmd, "@expires", UtcInstantText.Format(request.ExpiresAt));
                    AddParameter(cmd, "@created", UtcInstantText.Format(createdAt));
                    AddParameter(cmd, "@updated", UtcInstantText.Format(createdAt));

                    _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return new BudgetReservation(
                        id,
                        request.RunId,
                        request.BudgetPeriod,
                        request.ReservedUsd,
                        0m,
                        BudgetReservationStatus.Reserved,
                        request.ExpiresAt,
                        createdAt);
                },
                cancellationToken).ConfigureAwait(false);

            return Result<BudgetReservation>.Success(reservation);
        }
        catch (BudgetExceededException ex)
        {
            return Result<BudgetReservation>.Failure(new Error(
                ErrorCodes.Budget.Exceeded,
                BudgetErrorMessages.WouldExceed(ex.Period, ex.DailyLimit, ex.Current, ex.Additional, ex.AdditionalLabel, ex.Projected, ex.ReplacedReservation)));
        }
    }

    public async Task<Result> AdjustAsync(
        Guid reservationId,
        decimal reservedUsd,
        CancellationToken cancellationToken = default)
    {
        BudgetSettings budget = settings.CurrentValue.ResolveBudget();
        if (!budget.Enabled || budget.DailyLimitUsd <= 0)
        {
            return Result.Success();
        }

        decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);
        decimal requested = Math.Max(0m, reservedUsd);

        try
        {
            await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    string? hintedPeriod = await ReadReservationPeriodHintAsync(connection, reservationId, requested, cancellationToken).ConfigureAwait(false);

                    using CommittedReadAhead? committedHint = hintedPeriod is null ? null
                        : await ReadCommittedAheadAsync(connection, hintedPeriod, cancellationToken).ConfigureAwait(false);

                    if (BeforeWriteTransactionForTesting is not null)
                    {
                        await BeforeWriteTransactionForTesting(cancellationToken).ConfigureAwait(false);
                    }

                    // BeginTransaction(deferred: false) is BEGIN IMMEDIATE with a disposal-time rollback, so an
                    // already-cancelled token can never strand an open write transaction on the scoped connection.
                    await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                    decimal currentReserved = 0m;
                    string budgetPeriod = string.Empty;
                    BudgetReservationStatus status = BudgetReservationStatus.Released;
                    bool reservationFound;
                    await using (DbCommand read = connection.CreateCommand())
                    {
                        read.Transaction = transaction;
                        read.CommandText =
                            """
                            SELECT "ReservedUsd", "BudgetPeriod", "Status"
                            FROM "BudgetReservations"
                            WHERE "Id" = @id
                            """;
                        AddParameter(read, "@id", reservationId.ToString("N"));
                        await using DbDataReader reader = await read
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);
                        reservationFound = await reader
                            .ReadAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (reservationFound)
                        {
                            currentReserved = ExactUsdText.Read(reader, 0);
                            budgetPeriod = reader.GetString(1);
                            status = (BudgetReservationStatus)reader.GetInt32(2);
                        }
                    }

                    if (!reservationFound
                        || status != BudgetReservationStatus.Reserved
                        || requested <= currentReserved)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    decimal committed = await ReadCommittedForDecisionAsync(
                            connection,
                            transaction,
                            budgetPeriod,
                            committedHint,
                            cancellationToken)
                        .ConfigureAwait(false);
                    decimal outstanding = await SumOutstandingAsync(
                            connection,
                            transaction,
                            budgetPeriod,
                            cancellationToken)
                        .ConfigureAwait(false);
                    decimal projected = checked(
                        ExactUsdText.CheckedAdd(committed, outstanding) - currentReserved + requested);

                    if (projected > dailyLimit)
                    {
                        throw new BudgetExceededException(
                            dailyLimit,
                            ExactUsdText.CheckedAdd(committed, outstanding),
                            requested,
                            budgetPeriod,
                            "requested reservation",
                            projected,
                            currentReserved);
                    }

                    string today = TodayBudgetPeriod();

                    // After UTC midnight the raised amount is what the next call may spend, and that
                    // spend lands in today, whose ledger does not hold this reservation.
                    if (!string.Equals(today, budgetPeriod, StringComparison.Ordinal))
                    {
                        decimal todayCommitted = await SumCommittedAsync(
                                connection,
                                transaction,
                                today,
                                cancellationToken)
                            .ConfigureAwait(false);
                        decimal todayOutstanding = await SumOutstandingAsync(
                                connection,
                                transaction,
                                today,
                                cancellationToken)
                            .ConfigureAwait(false);
                        decimal todayCommittedAndOutstanding = ExactUsdText.CheckedAdd(todayCommitted, todayOutstanding);

                        decimal todayProjected = ExactUsdText.CheckedAdd(todayCommittedAndOutstanding, requested);

                        if (todayProjected > dailyLimit)
                        {
                            throw new BudgetExceededException(
                                dailyLimit,
                                todayCommittedAndOutstanding,
                                requested,
                                today,
                                "requested reservation",
                                todayProjected);
                        }
                    }

                    await using DbCommand update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText =
                        """
                        UPDATE "BudgetReservations"
                        SET "ReservedUsd" = @reserved, "UpdatedAt" = @updated
                        WHERE "Id" = @id AND "Status" = @status
                        """;
                    AddParameter(update, "@id", reservationId.ToString("N"));
                    _ = ExactUsdText.AddParameter(update, "@reserved", requested);
                    AddParameter(update, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));
                    AddParameter(update, "@status", (int)BudgetReservationStatus.Reserved);
                    _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            return Result.Success();
        }
        catch (BudgetExceededException ex)
        {
            return Result.Failure(new Error(
                ErrorCodes.Budget.Exceeded,
                BudgetErrorMessages.WouldExceed(ex.Period, ex.DailyLimit, ex.Current, ex.Additional, ex.AdditionalLabel, ex.Projected, ex.ReplacedReservation)));
        }
    }

    public async Task<Result> RecheckDailyLimitAsync(
        Guid reservationId,
        decimal delegatedSpendUsd,
        CancellationToken cancellationToken = default)
    {
        BudgetSettings budget = settings.CurrentValue.ResolveBudget();
        if (!budget.Enabled || budget.DailyLimitUsd <= 0)
        {
            return Result.Success();
        }

        decimal dailyLimit = ArcanumSettingClamps.BudgetDailyLimitUsd(budget.DailyLimitUsd);
        decimal delegatedUsd = Math.Max(0m, delegatedSpendUsd);

        // No write transaction: a plain read on the scoped connection, the path the spend queries take.
        RecheckSpend? spend = await SqliteBusyRetry.ExecuteAsync(
                async () =>
                {
                    DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                    string? budgetPeriod = null;
                    decimal reservationOutstanding = 0m;
                    await using (DbCommand read = connection.CreateCommand())
                    {
                        read.CommandText =
                            """
                            SELECT "BudgetPeriod", "ReservedUsd", "ReconciledUsd"
                            FROM "BudgetReservations"
                            WHERE "Id" = @id AND "Status" = @reserved
                            """;
                        AddParameter(read, "@id", reservationId.ToString("N"));
                        AddParameter(read, "@reserved", (int)BudgetReservationStatus.Reserved);

                        await using DbDataReader reader = await read
                            .ExecuteReaderAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            budgetPeriod = reader.GetString(0);
                            reservationOutstanding = checked(ExactUsdText.Read(reader, 1) - ExactUsdText.Read(reader, 2));
                        }
                    }

                    if (budgetPeriod is null)
                    {
                        return (RecheckSpend?)null;
                    }

                    decimal committed = await SumCommittedAsync(connection, transaction: null, budgetPeriod, cancellationToken)
                        .ConfigureAwait(false);
                    decimal outstanding = await SumOutstandingAsync(connection, transaction: null, budgetPeriod, cancellationToken)
                        .ConfigureAwait(false);
                    decimal reservationPeriodSpend = ExactUsdText.CheckedAdd(committed, outstanding);
                    string today = TodayBudgetPeriod();

                    if (string.Equals(today, budgetPeriod, StringComparison.Ordinal))
                    {
                        return new RecheckSpend(budgetPeriod, reservationPeriodSpend, TodayPeriod: null, TodaySpend: null);
                    }

                    // A turn admitted before UTC midnight: its reservation stays in the admitted day,
                    // but every round it finishes now is committed to today, and so is the next call.
                    decimal todayCommitted = await SumCommittedAsync(connection, transaction: null, today, cancellationToken)
                        .ConfigureAwait(false);
                    decimal todayOutstanding = await SumOutstandingAsync(connection, transaction: null, today, cancellationToken)
                        .ConfigureAwait(false);
                    decimal todaySpend = ExactUsdText.CheckedAdd(
                        ExactUsdText.CheckedAdd(todayCommitted, todayOutstanding),
                        reservationOutstanding);

                    return new RecheckSpend(budgetPeriod, reservationPeriodSpend, today, todaySpend);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (spend is not { } local)
        {
            return Result.Success();
        }

        // Delegated spend is today's, so it counts toward the day the next call lands in: the
        // reservation's own day until midnight, the new day after it.
        decimal reservationPeriodTotal = local.TodaySpend is null
            ? ExactUsdText.CheckedAdd(local.ReservationPeriodSpend, delegatedUsd)
            : local.ReservationPeriodSpend;

        if (reservationPeriodTotal > dailyLimit)
        {
            return DailyLimitRecheckFailure(dailyLimit, local.ReservationPeriodSpend,
                local.TodaySpend is null ? delegatedUsd : 0m, local.ReservationPeriod, reservationPeriodTotal);
        }

        if (local.TodaySpend is { } todaySpend)
        {
            decimal todayTotal = ExactUsdText.CheckedAdd(todaySpend, delegatedUsd);

            if (todayTotal > dailyLimit)
            {
                return DailyLimitRecheckFailure(dailyLimit, todaySpend, delegatedUsd, local.TodayPeriod!, todayTotal);
            }
        }

        return Result.Success();
    }

    private static Result DailyLimitRecheckFailure(decimal dailyLimit, decimal current, decimal delegated, string period, decimal projected) =>
        Result.Failure(new Error(
            ErrorCodes.Budget.Exceeded,
            BudgetErrorMessages.WouldExceed(period, dailyLimit, current, delegated, "delegated", projected)));

    public Task ExtendExpiryAsync(Guid reservationId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                // Forward only, and only while Reserved: a renewal racing a reconcile or a sweep must
                // never revive a settled reservation or pull a later expiry back.
                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "ExpiresAt" = @expires, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "Status" = @reserved AND "ExpiresAt" < @expires
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                AddParameter(cmd, "@expires", UtcInstantText.Format(expiresAt));
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task MarkPossiblyDispatchedAsync(Guid reservationId, Guid runId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(async () =>
        {
            DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using DbCommand cmd = connection.CreateCommand();

            cmd.CommandText = """
                UPDATE "BudgetReservations" SET "Status" = @uncertain, "UpdatedAt" = @updated
                WHERE "Id" = @id AND "RunId" = @run AND "Status" IN (@reserved, @uncertain)
                """;

            AddParameter(cmd, "@id", reservationId.ToString("N"));

            AddParameter(cmd, "@run", runId.ToString("N"));

            AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);

            AddParameter(cmd, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);

            AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

            if (await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The owned reservation is no longer available for dispatch.");
            }
        }, cancellationToken);
    }

    public Task ReconcileRecordedUsageAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(async () =>
        {
            SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            decimal? measured = null;

            await using (DbCommand read = connection.CreateCommand())
            {
                read.Transaction = transaction;

                // Evaluation owns exactly one physical call per run. A live or unrelated run is
                // never settled by this recovery seam, nor is a partial multi-call ledger.
                read.CommandText = """
                    SELECT bill.ActualCostUsd
                    FROM BillableOperations bill JOIN InferenceRuns run ON run.Id = bill.RunId
                    WHERE run.Id = @run AND run.Surface = 'memory-evaluation'
                      AND run.Purpose = 'counterfactual-arm' AND run.Status <> @running
                      AND bill.Status = @completed
                      AND (SELECT count(*) FROM BillableOperations allBills WHERE allBills.RunId = run.Id) = 1
                    """;

                AddParameter(read, "@run", runId.ToString("N"));

                AddParameter(read, "@running", (int)InferenceRunStatus.Running);

                AddParameter(read, "@completed", (int)BillableOperationStatus.Completed);

                await using DbDataReader reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) measured = ExactUsdText.Read(reader, 0);
            }

            if (measured is decimal actual)
            {
                await using DbCommand update = connection.CreateCommand();

                update.Transaction = transaction;

                update.CommandText = """
                    UPDATE BudgetReservations SET Status = @reconciled, ReconciledUsd = @actual, UpdatedAt = @now
                    WHERE RunId = @run AND Status = @uncertain
                    """;

                AddParameter(update, "@run", runId.ToString("N"));

                AddParameter(update, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);

                AddParameter(update, "@reconciled", (int)BudgetReservationStatus.Reconciled);

                ExactUsdText.AddParameter(update, "@actual", actual);

                AddParameter(update, "@now", UtcInstantText.Format(DateTimeOffset.UtcNow));

                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task ReconcileAsync(Guid reservationId, decimal actualCostUsd, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "ReconciledUsd" = @actual, "Status" = @status, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "Status" IN (@reserved, @uncertain)
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                _ = ExactUsdText.AddParameter(cmd, "@actual", Math.Max(0m, actualCostUsd));
                AddParameter(cmd, "@status", (int)BudgetReservationStatus.Reconciled);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);
                AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "Status" = @status, "ReconciledUsd" = @zero, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "Status" = @reserved
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                AddParameter(cmd, "@status", (int)BudgetReservationStatus.Released);
                _ = ExactUsdText.AddParameter(cmd, "@zero", 0m);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@updated", UtcInstantText.Format(DateTimeOffset.UtcNow));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default)
    {
        string period = TodayBudgetPeriod();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                return await SumCommittedAsync(connection, transaction: null, period, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<decimal> GetTodayOutstandingReservationsAsync(CancellationToken cancellationToken = default)
    {
        string period = TodayBudgetPeriod();

        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                return await SumOutstandingAsync(connection, transaction: null, period, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<int> SweepExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellationToken = default)
    {
        return SqliteBusyRetry.ExecuteAsync(
            async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await using DbCommand cmd = connection.CreateCommand();

                cmd.CommandText =
                    """
                    UPDATE "BudgetReservations"
                    SET "Status" = @expired, "UpdatedAt" = @updated
                    WHERE "Status" = @reserved AND "ExpiresAt" < @now
                    """;

                AddParameter(cmd, "@expired", (int)BudgetReservationStatus.Expired);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@now", UtcInstantText.Format(utcNow));
                AddParameter(cmd, "@updated", UtcInstantText.Format(utcNow));

                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static string UtcBudgetPeriod(DateTimeOffset utcNow) =>
        utcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The budget period a provider call made now lands its spend in.</summary>
    private string TodayBudgetPeriod() =>
        UtcBudgetPeriod(UtcNowForTesting?.Invoke() ?? DateTimeOffset.UtcNow);

    /// <summary>Worst-case reservation estimate for the current provider call.</summary>
    public static decimal EstimateWorstCaseTurnUsd(
        ModelPricingEntry pricing,
        int? maxOutputTokens = null,
        int? reasoningBudgetTokens = null,
        long? estimatedInputTokens = null) =>
        EstimateWorstCaseCallUsd(
            pricing,
            maxOutputTokens,
            reasoningBudgetTokens,
            estimatedInputTokens);

    /// <summary>
    /// Worst-case USD for one OpenAI batch JSONL line (single model call; batches force zero tools).
    /// </summary>
    public static decimal EstimateWorstCaseBatchLineUsd(
        ModelPricingEntry pricing,
        int? maxOutputTokens = null,
        int? reasoningBudgetTokens = null) =>
        EstimateWorstCaseCallUsd(
            pricing,
            maxOutputTokens,
            reasoningBudgetTokens,
            estimatedInputTokens: null);

    private static decimal EstimateWorstCaseCallUsd(
        ModelPricingEntry pricing,
        int? maxOutputTokens,
        int? reasoningBudgetTokens,
        long? estimatedInputTokens)
    {
        long requestedOutput = maxOutputTokens is > 0 ? maxOutputTokens.Value : 4096L;
        long requestedReasoning = reasoningBudgetTokens is > 0 ? reasoningBudgetTokens.Value : 0L;
        long maxPerCall = Math.Max(requestedOutput, requestedReasoning);
        long inputPerCall = estimatedInputTokens is > 0
            ? estimatedInputTokens.Value
            : maxPerCall;
        long outputPerCall = maxPerCall;
        decimal outputRate = Math.Max(0m, pricing.OutputPer1M);
        decimal reasoningRate = Math.Max(0m, pricing.ReasoningPer1M ?? outputRate);
        long conservativelyPricedReasoning =
            reasoningRate > outputRate ? Math.Min(requestedReasoning, outputPerCall) : 0L;

        return CostCalculator.CalculateCost(
            inputTokens: inputPerCall,
            outputTokens: outputPerCall,
            cachedTokens: 0L,
            reasoningTokens: conservativelyPricedReasoning,
            pricing);
    }

    /// <summary>
    /// Worst-case USD for an embedding batch sized by approximate input tokens.
    /// </summary>
    public static decimal EstimateWorstCaseEmbeddingUsd(ModelPricingEntry pricing, long inputTokens) =>
        CostCalculator.CalculateCost(
            inputTokens: Math.Max(0L, inputTokens),
            outputTokens: 0L,
            cachedTokens: 0L,
            pricing);

    // A speculative subtotal never owns a decision. Only the existing writer may validate it,
    // or discard it and read the authoritative rows itself. No value survives this invocation.
    private static async Task<CommittedReadAhead?> ReadCommittedAheadAsync(
        SqliteConnection connection, string period, CancellationToken cancellationToken)
    {
        CommittedReadAhead hint = new(connection, period);

        bool retained = false;

        try
        {
            CommittedReadStamp? before = await ReadCommittedStampAsync(connection, null, cancellationToken).ConfigureAwait(false);

            if (before is null || !hint.Uninterrupted) return null;

            decimal total = await SumCommittedAsync(connection, null, period, cancellationToken).ConfigureAwait(false);

            CommittedReadStamp? after = await ReadCommittedStampAsync(connection, null, cancellationToken).ConfigureAwait(false);

            if (after != before || !hint.Uninterrupted) return null;

            hint.Stamp = before.Value;

            hint.Total = total;

            retained = true;

            return hint;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is FormatException or OverflowException or ArgumentOutOfRangeException or InvalidDataException or SqliteException)
        {
            // In particular, bad speculative money must not turn an authoritative Adjust no-op
            // into an error. If a value is needed, the unchanged writer fold decides or throws.
            return null;
        }
        finally
        {
            if (!retained) hint.Dispose();
        }
    }

    private static async Task<decimal> ReadCommittedForDecisionAsync(
        SqliteConnection connection, SqliteTransaction transaction, string period,
        CommittedReadAhead? hint, CancellationToken cancellationToken)
    {
        if (hint is not null && hint.Uninterrupted && string.Equals(hint.Period, period, StringComparison.Ordinal))
        {
            CommittedReadStamp? current = null;

            try
            {
                current = await ReadCommittedStampAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) when (!cancellationToken.IsCancellationRequested)
            {
                // An unavailable optional proof can never bless the subtotal.
            }

            if (hint.Uninterrupted && current == hint.Stamp) return hint.Total;
        }

        return await SumCommittedAsync(connection, transaction, period, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadReservationPeriodHintAsync(
        SqliteConnection connection, Guid reservationId, decimal requested, CancellationToken cancellationToken)
    {
        try
        {
            await using DbCommand command = connection.CreateCommand();

            command.CommandText = "SELECT \"ReservedUsd\", \"BudgetPeriod\" FROM \"BudgetReservations\" WHERE \"Id\" = @id AND \"Status\" = @reserved";

            AddParameter(command, "@id", reservationId.ToString("N"));

            AddParameter(command, "@reserved", (int)BudgetReservationStatus.Reserved);

            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            // Avoid a day-sized speculative read for an ordinary no-raise. This only selects
            // optional work: the current row and every decision are still reread under writer.
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                && requested > ExactUsdText.Read(reader, 0) ? reader.GetString(1) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && exception is FormatException or OverflowException or InvalidDataException or SqliteException)
        {
            return null;
        }
    }

    private static async Task<CommittedReadStamp?> ReadCommittedStampAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.Transaction = transaction;

        command.CommandText = "SELECT (SELECT schema_version FROM pragma_schema_version), (SELECT data_version FROM pragma_data_version), total_changes();";

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            && reader.GetValue(0) is long schema && schema >= 0
            && reader.GetValue(1) is long data && data >= 0
            && reader.GetValue(2) is long changes && changes >= 0
            ? new CommittedReadStamp(schema, data, changes) : null;
    }

    private readonly record struct CommittedReadStamp(long Schema, long Data, long Changes);

    private sealed class CommittedReadAhead : IDisposable
    {
        private readonly SqliteConnection _connection;

        private readonly object? _nativeHandle;

        private volatile bool _interrupted;

        private bool _disposed;

        internal CommittedReadAhead(SqliteConnection connection, string period)
        {
            _connection = connection;

            Period = period;

            connection.StateChange += ConnectionStateChanged;

            _nativeHandle = connection.Handle;
        }

        internal string Period { get; }

        internal CommittedReadStamp Stamp { get; set; }

        internal decimal Total { get; set; }

        internal bool Uninterrupted => !_disposed && !_interrupted && _connection.State == ConnectionState.Open
            && _nativeHandle is not null && ReferenceEquals(_nativeHandle, _connection.Handle);

        private void ConnectionStateChanged(object? sender, StateChangeEventArgs change) => _interrupted = true;

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            _connection.StateChange -= ConnectionStateChanged;
        }
    }

    private static async Task<decimal> SumCommittedAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string budgetPeriod,
        CancellationToken cancellationToken)
    {
        // Committed spend = billable ops completed on this UTC day. Per DESIGN §22.2 the day's spend
        // authority is BillableOperations plus outstanding BudgetReservations; "CostAdjustments" rows
        // are deliberately NOT summed here and never move the reservation ceiling.
        DateTimeOffset start = DateTimeOffset.Parse(
            budgetPeriod + "T00:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        string dayStart = UtcInstantText.Format(start);

        string dayEnd = UtcInstantText.Format(start.AddDays(1));

        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT "ActualCostUsd"
            FROM "BillableOperations"
            WHERE "CompletedAt" >= @dayStart AND "CompletedAt" < @dayEnd
            """;

        AddParameter(cmd, "@dayStart", dayStart);
        AddParameter(cmd, "@dayEnd", dayEnd);

        decimal total = 0m;

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            total = ExactUsdText.CheckedAdd(total, ExactUsdText.Read(reader, 0));
        }

        return total;
    }

    private static async Task<decimal> SumOutstandingAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string budgetPeriod,
        CancellationToken cancellationToken)
    {
        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT "ReservedUsd", "ReconciledUsd"
            FROM "BudgetReservations"
            WHERE ("BudgetPeriod" = @period AND "Status" = @reserved) OR "Status" = @uncertain
            """;

        AddParameter(cmd, "@period", budgetPeriod);
        AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
        AddParameter(cmd, "@uncertain", (int)BudgetReservationStatus.UncertainPaid);

        decimal total = 0m;

        await using DbDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            decimal outstanding = checked(ExactUsdText.Read(reader, 0) - ExactUsdText.Read(reader, 1));

            total = ExactUsdText.CheckedAdd(total, outstanding);
        }

        return total;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        DbConnection connection = db.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        return (SqliteConnection)connection;
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        DbParameter p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// What a recheck judges: the reservation's own budget period, and today when that is another
    /// day (null on the reservation's own day). Today's figure already holds this reservation.
    /// </summary>
    private readonly record struct RecheckSpend(string ReservationPeriod, decimal ReservationPeriodSpend, string? TodayPeriod, decimal? TodaySpend);

    private sealed class BudgetExceededException(decimal dailyLimit, decimal current, decimal additional, string period, string additionalLabel, decimal projected, decimal? replacedReservation = null) : Exception
    {
        public decimal DailyLimit { get; } = dailyLimit;

        public decimal Current { get; } = current;

        public decimal Additional { get; } = additional;

        public string Period { get; } = period;

        public string AdditionalLabel { get; } = additionalLabel;

        public decimal Projected { get; } = projected;

        public decimal? ReplacedReservation { get; } = replacedReservation;
    }
}
