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
/// Atomic budget reservations with tiny <c>BEGIN IMMEDIATE</c> transactions.
/// </summary>
internal sealed class BudgetReservationService(
    ArcanumDbContext db,
    IOptionsMonitor<ArcanumSettings> settings) : IBudgetReservationService
{

    public async Task<Result<BudgetReservation>> ReserveAsync(
        BudgetReservationRequest request,
        CancellationToken cancellationToken = default)
    {
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

                    // BeginTransaction(deferred: false) is BEGIN IMMEDIATE with a disposal-time rollback, so an
                    // already-cancelled token can never strand an open write transaction on the scoped connection.
                    await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

                    decimal committed = await SumCommittedAsync(connection, transaction, request.BudgetPeriod, cancellationToken)
                        .ConfigureAwait(false);

                    decimal outstanding = await SumOutstandingAsync(connection, transaction, request.BudgetPeriod, cancellationToken)
                        .ConfigureAwait(false);

                    if (committed + outstanding + request.ReservedUsd > dailyLimit)
                    {
                        throw new BudgetExceededException(dailyLimit, committed + outstanding);
                    }

                    await using DbCommand cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText =
                        """
                        INSERT INTO "BudgetReservations"
                            ("Id", "RunId", "BudgetPeriod", "ReservedUsd", "ReconciledUsd", "Status", "ExpiresAt", "CreatedAt", "UpdatedAt")
                        VALUES
                            (@id, @runId, @period, @reserved, 0, @status, @expires, @created, @updated)
                        """;

                    AddParameter(cmd, "@id", id.ToString("N"));
                    AddParameter(cmd, "@runId", request.RunId.ToString("N"));
                    AddParameter(cmd, "@period", request.BudgetPeriod);
                    AddParameter(cmd, "@reserved", request.ReservedUsd);
                    AddParameter(cmd, "@status", (int)BudgetReservationStatus.Reserved);
                    AddParameter(cmd, "@expires", request.ExpiresAt.ToString("o", CultureInfo.InvariantCulture));
                    AddParameter(cmd, "@created", createdAt.ToString("o", CultureInfo.InvariantCulture));
                    AddParameter(cmd, "@updated", createdAt.ToString("o", CultureInfo.InvariantCulture));

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
                $"Daily budget limit of ${ex.DailyLimit:0.00} USD would be exceeded (committed+reserved: ${ex.Current:0.00} USD)."));
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
                            currentReserved = Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture);
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

                    decimal committed = await SumCommittedAsync(
                            connection,
                            transaction,
                            budgetPeriod,
                            cancellationToken)
                        .ConfigureAwait(false);
                    decimal outstanding = await SumOutstandingAsync(
                            connection,
                            transaction,
                            budgetPeriod,
                            cancellationToken)
                        .ConfigureAwait(false);
                    decimal projected = committed + outstanding - currentReserved + requested;
                    if (projected > dailyLimit)
                    {
                        throw new BudgetExceededException(dailyLimit, committed + outstanding);
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
                    AddParameter(update, "@reserved", requested);
                    AddParameter(update, "@updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
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
                $"Daily budget limit of ${ex.DailyLimit:0.00} USD would be exceeded (committed+reserved: ${ex.Current:0.00} USD)."));
        }
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
                    WHERE "Id" = @id AND "Status" = @reserved
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                AddParameter(cmd, "@actual", Math.Max(0m, actualCostUsd));
                AddParameter(cmd, "@status", (int)BudgetReservationStatus.Reconciled);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

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
                    SET "Status" = @status, "ReconciledUsd" = 0, "UpdatedAt" = @updated
                    WHERE "Id" = @id AND "Status" = @reserved
                    """;

                AddParameter(cmd, "@id", reservationId.ToString("N"));
                AddParameter(cmd, "@status", (int)BudgetReservationStatus.Released);
                AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);
                AddParameter(cmd, "@updated", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

                _ = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public Task<decimal> GetTodayCommittedSpendAsync(CancellationToken cancellationToken = default)
    {
        string period = UtcBudgetPeriod(DateTimeOffset.UtcNow);

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
        string period = UtcBudgetPeriod(DateTimeOffset.UtcNow);

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
                AddParameter(cmd, "@now", utcNow.ToString("o", CultureInfo.InvariantCulture));
                AddParameter(cmd, "@updated", utcNow.ToString("o", CultureInfo.InvariantCulture));

                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);
    }

    public static string UtcBudgetPeriod(DateTimeOffset utcNow) =>
        utcNow.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static async Task<decimal> SumCommittedAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string budgetPeriod,
        CancellationToken cancellationToken)
    {
        // Committed spend = billable ops completed on this UTC day. Per DESIGN §22.2 the day's spend
        // authority is BillableOperations plus outstanding BudgetReservations; "CostAdjustments" rows
        // are deliberately NOT summed here and never move the reservation ceiling.
        string dayStart = budgetPeriod + "T00:00:00.0000000+00:00";
        string dayEnd = DateTimeOffset.Parse(budgetPeriod + "T00:00:00Z", CultureInfo.InvariantCulture)
            .AddDays(1)
            .ToString("o", CultureInfo.InvariantCulture);

        await using DbCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT COALESCE(SUM("ActualCostUsd"), 0)
            FROM "BillableOperations"
            WHERE "CompletedAt" >= @dayStart AND "CompletedAt" < @dayEnd
            """;

        AddParameter(cmd, "@dayStart", dayStart);
        AddParameter(cmd, "@dayEnd", dayEnd);

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToDecimal(scalar, CultureInfo.InvariantCulture);
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
            SELECT COALESCE(SUM("ReservedUsd" - "ReconciledUsd"), 0)
            FROM "BudgetReservations"
            WHERE "BudgetPeriod" = @period AND "Status" = @reserved
            """;

        AddParameter(cmd, "@period", budgetPeriod);
        AddParameter(cmd, "@reserved", (int)BudgetReservationStatus.Reserved);

        object? scalar = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return Convert.ToDecimal(scalar, CultureInfo.InvariantCulture);
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

    private sealed class BudgetExceededException(decimal dailyLimit, decimal current) : Exception
    {
        public decimal DailyLimit { get; } = dailyLimit;

        public decimal Current { get; } = current;
    }

}
