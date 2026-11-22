using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed partial class BudgetReservationServiceTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    private readonly Xunit.Abstractions.ITestOutputHelper _snapshotOutput;

    public BudgetReservationServiceTests(GrimoireFixture fixture, Xunit.Abstractions.ITestOutputHelper snapshotOutput)
    {
        _fixture = fixture;

        _snapshotOutput = snapshotOutput;
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
    public async Task UncertainPaid_RecoveryPreservesUnknownButSettlesDurableMeasuredBillOnce()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid run = await new TurnRunWriter(_db!).StartRunAsync(new("evaluation:0", null, "memory-evaluation", "counterfactual-arm", null, now));

        var held = await LegacyBudgetFixture.SeedAsync(_db!, new(run, 8m, now.AddHours(1), BudgetReservationService.UtcBudgetPeriod(now)));

        await service.MarkPossiblyDispatchedAsync(held.Id, run);

        await service.ReconcileRecordedUsageAsync(run);

        Assert.Equal(8m, await service.GetTodayOutstandingReservationsAsync());

        await InsertBillableOperationAsync(run, now, 2m);

        Assert.Equal(8m, await service.GetTodayOutstandingReservationsAsync());

        Assert.Equal(2m, await service.GetTodayCommittedSpendAsync());

        await using ArcanumDbContext recoveredDb = _fixture.CreateContext(_dbPath);

        var recovered = CreateService(recoveredDb);

        await recovered.ReconcileRecordedUsageAsync(run);

        Assert.Equal(8m, await recovered.GetTodayOutstandingReservationsAsync());

        Assert.True(await new TurnRunWriter(recoveredDb).TryAbandonRunAsync(run));

        await recovered.ReconcileRecordedUsageAsync(run);

        await recovered.ReconcileRecordedUsageAsync(run);

        Assert.Equal(0m, await recovered.GetTodayOutstandingReservationsAsync());

        Assert.Equal(2m, await recovered.GetTodayCommittedSpendAsync());

        Assert.Equal((BudgetReservationStatus.Reconciled, 2m), await ReadReservationStateAsync(held.Id));
    }

    [SkippableFact]
    public async Task UncertainPaid_SurvivesRecoveryExpiryAndUtcRolloverWithoutInventingActualSpend()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService();

        DateTimeOffset yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        BudgetReservation held = await SeedLegacyReservationAsync(8m, yesterday.AddHours(1), BudgetReservationService.UtcBudgetPeriod(yesterday));

        await service.MarkPossiblyDispatchedAsync(held.Id, held.RunId);

        await service.MarkPossiblyDispatchedAsync(held.Id, held.RunId);

        await using ArcanumDbContext recoveredDb = _fixture.CreateContext(_dbPath);

        BudgetReservationService recovered = CreateService(recoveredDb);

        await recovered.ReleaseAsync(held.Id);

        Assert.Equal(0, await recovered.SweepExpiredAsync(DateTimeOffset.UtcNow.AddDays(3)));

        Assert.Equal(8m, await recovered.GetTodayOutstandingReservationsAsync());

        Assert.Equal(0m, await recovered.GetTodayCommittedSpendAsync());

        Assert.Equal((BudgetReservationStatus.UncertainPaid, 0m), await ReadReservationStateAsync(held.Id));

        await recovered.ReconcileAsync(held.Id, 2m);

        await recovered.ReconcileAsync(held.Id, 7m);

        Assert.Equal((BudgetReservationStatus.Reconciled, 2m), await ReadReservationStateAsync(held.Id));

        Assert.Equal(0m, await recovered.GetTodayOutstandingReservationsAsync());
    }

    [SkippableFact]
    public async Task UncertainPaid_RefusesWrongOwnerAndInactiveReservation()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService();

        BudgetReservation held = await SeedLegacyReservationAsync(8m, DateTimeOffset.UtcNow.AddHours(1), BudgetReservationService.UtcBudgetPeriod(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkPossiblyDispatchedAsync(held.Id, Guid.NewGuid()));

        await service.ReleaseAsync(held.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MarkPossiblyDispatchedAsync(held.Id, held.RunId));
    }

    [SkippableFact]
    public async Task SpendQueries_WhenLedgerIsEmpty_ReturnZeroAndOpenClosedConnection()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService();

        await _db!.Database.CloseConnectionAsync();
        Assert.Equal(ConnectionState.Closed, _db.Database.GetDbConnection().State);

        decimal committed = await service.GetTodayCommittedSpendAsync();

        Assert.Equal(0m, committed);
        Assert.Equal(ConnectionState.Open, _db.Database.GetDbConnection().State);
        Assert.Equal(0m, await service.GetTodayOutstandingReservationsAsync());
    }

    [SkippableFact]
    public async Task MeasuredSpendCountsOnlyBillableOperationsOnTheUtcDay()
    {
        RequireSqlCipher();

        DateTimeOffset dayStart = new(2035, 4, 7, 0, 0, 0, TimeSpan.Zero);
        TurnRunWriter runs = new(_db!);
        Guid runId = await runs.StartRunAsync(new InferenceRunStart(
            RequestId: "budget-adjustments",
            SessionId: null,
            Surface: "test",
            Purpose: "coverage",
            IdempotencyClaimId: null,
            StartedAt: dayStart));

        await InsertBillableOperationAsync(runId, dayStart.AddHours(1), 0.60m);
        await InsertBillableOperationAsync(runId, dayStart.AddMilliseconds(-1), 30m);
        await InsertBillableOperationAsync(runId, dayStart.AddDays(1), 40m);
        await InsertCostAdjustmentAsync(runId, dayStart.AddHours(2), 50m);

        BudgetReservationService service = CreateService();

        service.UtcNowForTesting = () => dayStart.AddHours(4);

        Assert.Equal(0.60m, await service.GetTodayCommittedSpendAsync());
    }

    [SkippableFact]
    public async Task ReservationLifecycle_ReconcilesReleasesAndExpiresOnlyEligibleRows()
    {
        RequireSqlCipher();

        BudgetReservationService service = CreateService();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string period = BudgetReservationService.UtcBudgetPeriod(now);

        BudgetReservation reconciled = await SeedLegacyReservationAsync(1m, now.AddHours(1), period);
        BudgetReservation released = await SeedLegacyReservationAsync(2m, now.AddHours(1), period);
        BudgetReservation expired = await SeedLegacyReservationAsync(3m, now.AddTicks(-1), period);
        BudgetReservation live = await SeedLegacyReservationAsync(4m, now.AddHours(1), period);

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

    private async Task<Guid> StartRunAsync(string requestId, DateTimeOffset startedAt)
    {
        TurnRunWriter runs = new(_db!);

        return await runs.StartRunAsync(new InferenceRunStart(
            RequestId: requestId,
            SessionId: null,
            Surface: "test",
            Purpose: "coverage",
            IdempotencyClaimId: null,
            StartedAt: startedAt));
    }

    private async Task AssertConnectionIsNotInATransactionAsync()
    {
        await ExecuteAsync("DROP TRIGGER IF EXISTS cancel_reservation_write;");

        // An orphaned BEGIN IMMEDIATE on the scoped connection surfaces here as
        // "cannot start a transaction within a transaction".
        await ExecuteAsync("BEGIN IMMEDIATE;");
        await ExecuteAsync("ROLLBACK;");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;

        _ = await command.ExecuteNonQueryAsync();
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private BudgetReservationService CreateService() => CreateService(_db!);

    private static BudgetReservationService CreateService(ArcanumDbContext db) =>
        new(db);

    private Task<BudgetReservation> SeedLegacyReservationAsync(decimal amount, DateTimeOffset expiresAt, string period) =>
        LegacyBudgetFixture.SeedAsync(_db!, new(Guid.NewGuid(), amount, expiresAt, period));

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
        AddParameter(command, "@startedAt", UtcInstantText.Format(completedAt.AddSeconds(-1)));
        AddParameter(command, "@completedAt", UtcInstantText.Format(completedAt));
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
        AddParameter(command, "@createdAt", UtcInstantText.Format(createdAt));

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

    private async Task<string> ReadExpiresAtAsync(Guid id)
    {
        await using DbCommand command = _db!.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT "ExpiresAt"
            FROM "BudgetReservations"
            WHERE "Id" = @id;
            """;
        AddParameter(command, "@id", id.ToString("N"));

        object? scalar = await command.ExecuteScalarAsync();
        return Assert.IsType<string>(scalar);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
