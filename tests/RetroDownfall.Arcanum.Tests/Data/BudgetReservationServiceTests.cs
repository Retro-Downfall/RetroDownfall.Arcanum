using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class BudgetReservationServiceTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public BudgetReservationServiceTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();
        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();
            await _db.DisposeAsync();
            SqliteConnection.ClearPool(connection);
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task ReserveAsync_WhenBudgetSettingsAreMissing_ReturnsSyntheticReleasedReservation()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(budget: null);
        Guid runId = Guid.NewGuid();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        const string period = "2035-02-03";
        DateTimeOffset before = DateTimeOffset.UtcNow;

        Result<BudgetReservation> result = await service.ReserveAsync(
            new BudgetReservationRequest(runId, 17.25m, expiresAt, period));

        DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.Id);
        Assert.Equal(runId, result.Value.RunId);
        Assert.Equal(period, result.Value.BudgetPeriod);
        Assert.Equal(0m, result.Value.ReservedUsd);
        Assert.Equal(0m, result.Value.ReconciledUsd);
        Assert.Equal(BudgetReservationStatus.Released, result.Value.Status);
        Assert.Equal(expiresAt, result.Value.ExpiresAt);
        Assert.InRange(result.Value.CreatedAt, before, after);
        Assert.Equal(0L, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task ReserveAsync_WhenEnabledLimitIsZero_DoesNotPersistReservation()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = 0m,
        });
        Guid runId = Guid.NewGuid();

        Result<BudgetReservation> result = await service.ReserveAsync(
            new BudgetReservationRequest(
                runId,
                ReservedUsd: 2m,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
                BudgetPeriod: "2035-02-03"));

        Assert.True(result.IsSuccess);
        Assert.Equal(runId, result.Value.RunId);
        Assert.Equal(BudgetReservationStatus.Released, result.Value.Status);
        Assert.Equal(0m, result.Value.ReservedUsd);
        Assert.Equal(0L, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task SpendQueries_WhenLedgerIsEmpty_ReturnZeroAndOpenClosedConnection()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = 100m,
        });

        await _db!.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, _db.Database.GetDbConnection().State);

        decimal committed = await service.GetTodayCommittedSpendAsync();

        Assert.Equal(0m, committed);
        Assert.Equal(ConnectionState.Open, _db.Database.GetDbConnection().State);
        Assert.Equal(0m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableFact]
    public async Task ReserveAsync_UsesUtcLedgerBoundariesAndAllowsExactLimit()
    {
        RequireSqlCipher();

        DateTimeOffset dayStart = new(2035, 2, 3, 0, 0, 0, TimeSpan.Zero);
        string period = BudgetReservationService.UtcBudgetPeriod(dayStart);
        TurnRunWriter runs = new(_db!);
        Guid runId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: "budget-boundary",
            SessionId: null,
            Surface: "test",
            Purpose: "coverage",
            IdempotencyClaimId: null,
            StartedAt: dayStart.AddHours(-1)));

        await InsertBillableOperationAsync(runId, dayStart.AddTicks(-1), 50m);
        await InsertBillableOperationAsync(runId, dayStart, 0.20m);
        await InsertBillableOperationAsync(runId, dayStart.AddDays(1).AddTicks(-1), 0.30m);
        await InsertBillableOperationAsync(runId, dayStart.AddDays(1), 75m);

        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = 1m,
        });

        Result<BudgetReservation> exactLimit = await service.ReserveAsync(
            new BudgetReservationRequest(
                Guid.NewGuid(),
                ReservedUsd: 0.50m,
                ExpiresAt: dayStart.AddHours(1),
                BudgetPeriod: period));
        Result<BudgetReservation> overLimit = await service.ReserveAsync(
            new BudgetReservationRequest(
                Guid.NewGuid(),
                ReservedUsd: 0.01m,
                ExpiresAt: dayStart.AddHours(1),
                BudgetPeriod: period));

        Assert.True(exactLimit.IsSuccess);
        Assert.Equal(0.50m, exactLimit.Value.ReservedUsd);
        Assert.True(overLimit.IsFailure);
        Assert.Equal(ErrorCodes.Budget.Exceeded, overLimit.Error.Code);
        Assert.Equal(
            "Daily budget limit of $1.00 USD would be exceeded (committed+reserved: $1.00 USD).",
            overLimit.Error.Message);
        Assert.Equal(1L, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task ReserveAsync_CountsOnlyBillableOperationsAndIgnoresCostAdjustments()
    {
        RequireSqlCipher();

        DateTimeOffset dayStart = new(2035, 4, 7, 0, 0, 0, TimeSpan.Zero);
        string period = BudgetReservationService.UtcBudgetPeriod(dayStart);
        TurnRunWriter runs = new(_db!);
        Guid runId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: "budget-adjustments",
            SessionId: null,
            Surface: "test",
            Purpose: "coverage",
            IdempotencyClaimId: null,
            StartedAt: dayStart));

        await InsertBillableOperationAsync(runId, dayStart.AddHours(1), 0.60m);
        await InsertCostAdjustmentAsync(runId, dayStart.AddHours(2), 50m);

        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = 1m,
        });

        Result<BudgetReservation> result = await service.ReserveAsync(
            new BudgetReservationRequest(
                Guid.NewGuid(),
                ReservedUsd: 0.40m,
                ExpiresAt: dayStart.AddHours(3),
                BudgetPeriod: period));

        Assert.True(result.IsSuccess, result.Error.Message);
        Assert.Equal(BudgetReservationStatus.Reserved, result.Value.Status);
        Assert.Equal(1L, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task ReservationLifecycle_ReconcilesReleasesAndExpiresOnlyEligibleRows()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = 100m,
        });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string period = BudgetReservationService.UtcBudgetPeriod(now);

        BudgetReservation reconciled = await ReserveAsync(service, 1m, now.AddHours(1), period);
        BudgetReservation released = await ReserveAsync(service, 2m, now.AddHours(1), period);
        BudgetReservation expired = await ReserveAsync(service, 3m, now.AddTicks(-1), period);
        BudgetReservation live = await ReserveAsync(service, 4m, now.AddHours(1), period);

        Assert.Equal(10m, await service.GetTodayOutstandingReservationsAsync());

        await service.ReconcileAsync(reconciled.Id, actualCostUsd: -5m);
        await service.ReconcileAsync(reconciled.Id, actualCostUsd: 9m);
        await service.ReleaseAsync(released.Id);
        await service.ReconcileAsync(released.Id, actualCostUsd: 8m);
        int swept = await service.SweepExpiredAsync(now);

        Assert.Equal(1, swept);
        Assert.Equal(
            (BudgetReservationStatus.Reconciled, 0m),
            await ReadReservationStateAsync(reconciled.Id));
        Assert.Equal(
            (BudgetReservationStatus.Released, 0m),
            await ReadReservationStateAsync(released.Id));
        Assert.Equal(
            (BudgetReservationStatus.Expired, 0m),
            await ReadReservationStateAsync(expired.Id));
        Assert.Equal(
            (BudgetReservationStatus.Reserved, 0m),
            await ReadReservationStateAsync(live.Id));
        Assert.Equal(4m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableFact]
    public async Task AdjustAsync_AtomicallyRaisesWithoutLoweringOrExceedingDailyLimit()
    {
        RequireSqlCipher();
        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = true,
            DailyLimitUsd = 10m,
        });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string period = BudgetReservationService.UtcBudgetPeriod(now);
        BudgetReservation first = await ReserveAsync(service, 2m, now.AddHours(1), period);

        Result raised = await service.AdjustAsync(first.Id, 5m);
        Result ignoredLower = await service.AdjustAsync(first.Id, 1m);
        _ = await ReserveAsync(service, 4m, now.AddHours(1), period);
        Result rejected = await service.AdjustAsync(first.Id, 7m);

        Assert.True(raised.IsSuccess);
        Assert.True(ignoredLower.IsSuccess);
        Assert.True(rejected.IsFailure);
        Assert.Equal(ErrorCodes.Budget.Exceeded, rejected.Error.Code);
        Assert.Equal(9m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableTheory]
    [InlineData(false, 100)]
    [InlineData(true, 0)]
    public async Task AdjustAsync_WhenBudgetDisabledOrLimitNonPositive_ReturnsWithoutPersistence(
        bool enabled,
        double dailyLimit)
    {
        RequireSqlCipher();
        BudgetReservationService service = CreateService(new BudgetPolicySettings
        {
            Enabled = enabled,
            DailyLimitUsd = (decimal)dailyLimit,
        });

        Result result = await service.AdjustAsync(Guid.NewGuid(), reservedUsd: 5m);

        Assert.True(result.IsSuccess);
        Assert.Equal(0L, await CountReservationsAsync());
    }

    [SkippableFact]
    public async Task AdjustAsync_WhenBudgetSettingsMissing_ReturnsWithoutPersistence()
    {
        RequireSqlCipher();
        BudgetReservationService service = CreateService(budget: null);

        Result result = await service.AdjustAsync(Guid.NewGuid(), reservedUsd: 5m);

        Assert.True(result.IsSuccess);
        Assert.Equal(0L, await CountReservationsAsync());
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private BudgetReservationService CreateService(BudgetPolicySettings? budget)
    {
        ArcanumSettings settings = new()
        {
            Cost = new CostSettings
            {
                Budget = budget is null
                    ? null!
                    : budget,
            },
        };

        return new BudgetReservationService(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(settings));
    }

    private static async Task<BudgetReservation> ReserveAsync(
        BudgetReservationService service,
        decimal amount,
        DateTimeOffset expiresAt,
        string period)
    {
        Result<BudgetReservation> result = await service.ReserveAsync(
            new BudgetReservationRequest(Guid.NewGuid(), amount, expiresAt, period));

        Assert.True(result.IsSuccess, result.Error.Message);
        return result.Value;
    }

    private async Task InsertBillableOperationAsync(
        Guid runId,
        DateTimeOffset completedAt,
        decimal actualCostUsd)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            INSERT INTO "BillableOperations"
                ("Id", "RunId", "OperationType", "Provider", "Model", "Purpose",
                 "StartedAt", "CompletedAt", "InputTokens", "OutputTokens", "ReasoningTokens",
                 "CachedTokens", "PricingSnapshotJson", "ActualCostUsd", "Status", "ProviderRequestId")
            VALUES
                (@id, @runId, @operationType, 'test', 'model', 'coverage',
                 @startedAt, @completedAt, 1, 1, 0, 0, '{}', @actualCostUsd, @status, NULL);
            """;
        AddParameter(command, "@id", Guid.NewGuid().ToString("N"));
        AddParameter(command, "@runId", runId.ToString("N"));
        AddParameter(command, "@operationType", (int)BillableOperationType.Chat);
        AddParameter(command, "@startedAt", completedAt.AddSeconds(-1).ToString("o", CultureInfo.InvariantCulture));
        AddParameter(command, "@completedAt", completedAt.ToString("o", CultureInfo.InvariantCulture));
        AddParameter(command, "@actualCostUsd", actualCostUsd);
        AddParameter(command, "@status", (int)BillableOperationStatus.Completed);

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task InsertCostAdjustmentAsync(
        Guid runId,
        DateTimeOffset createdAt,
        decimal amountUsd)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            INSERT INTO "CostAdjustments"
                ("Id", "BillableOperationId", "RunId", "AmountUsd", "Reason", "CreatedAt")
            VALUES
                (@id, NULL, @runId, @amountUsd, 'coverage', @createdAt);
            """;
        AddParameter(command, "@id", Guid.NewGuid().ToString("N"));
        AddParameter(command, "@runId", runId.ToString("N"));
        AddParameter(command, "@amountUsd", amountUsd);
        AddParameter(command, "@createdAt", createdAt.ToString("o", CultureInfo.InvariantCulture));

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<long> CountReservationsAsync()
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText = """SELECT COUNT(*) FROM "BudgetReservations";""";

        object? scalar = await command.ExecuteScalarAsync();
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<(BudgetReservationStatus Status, decimal ReconciledUsd)> ReadReservationStateAsync(Guid id)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT "Status", "ReconciledUsd"
            FROM "BudgetReservations"
            WHERE "Id" = @id;
            """;
        AddParameter(command, "@id", id.ToString("N"));

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (
            (BudgetReservationStatus)reader.GetInt32(0),
            Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
