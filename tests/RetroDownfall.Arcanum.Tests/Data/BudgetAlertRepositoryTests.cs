using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class BudgetAlertRepositoryTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public BudgetAlertRepositoryTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();

        _db = _fixture.CreateContext(_dbPath);

        await _db.Database.CloseConnectionAsync();
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
    public async Task HasAlertedTodayAsync_from_closed_context_returns_false_without_matching_row()
    {
        RequireSqlCipher();

        BudgetAlertRepository repository = CreateRepository();

        Assert.Equal(ConnectionState.Closed, _db!.Database.GetDbConnection().State);

        bool alerted = await repository.HasAlertedTodayAsync(80);

        Assert.False(alerted);
    }

    [SkippableFact]
    public async Task RecordAlertAsync_persists_values_and_makes_threshold_visible_today()
    {
        RequireSqlCipher();

        BudgetAlertRepository repository = CreateRepository();

        DateTimeOffset before = DateTimeOffset.UtcNow;

        bool recorded = await repository.RecordAlertAsync(75, 12.3456m, 20.50m);

        DateTimeOffset after = DateTimeOffset.UtcNow;

        Assert.True(recorded);

        await _db!.Database.CloseConnectionAsync();

        Assert.True(await repository.HasAlertedTodayAsync(75));

        await _db.Database.CloseConnectionAsync();

        await using ArcanumDbContext verificationDb = _fixture.CreateContext(_dbPath);
        await using DbCommand command = verificationDb.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT "Threshold", "AlertedAt", "SpendUsd", "DailyLimitUsd"
            FROM "BudgetAlerts"
            WHERE "Threshold" = 75;
            """;

        await using DbDataReader reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal(75L, reader.GetInt64(0));

        DateTimeOffset alertedAt =
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.InRange(alertedAt, before, after);

        Assert.Equal(12.3456m, Convert.ToDecimal(reader.GetValue(2), CultureInfo.InvariantCulture));

        Assert.Equal(20.50m, Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture));

        Assert.False(await reader.ReadAsync());
    }

    [SkippableFact]
    public async Task RecordAlertAsync_duplicate_threshold_today_returns_false_without_constraint_exceptions_or_warnings()
    {
        RequireSqlCipher();

        CapturingLogger logger = new();

        BudgetAlertRepository repository = CreateRepository(logger);

        Assert.True(await repository.RecordAlertAsync(90, 18m, 20m));

        AsyncLocal<bool> observing = new() { Value = true };

        List<int> constraintExceptions = [];

        void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (observing.Value && args.Exception is SqliteException { SqliteErrorCode: 19 } exception)
                constraintExceptions.Add(exception.SqliteExtendedErrorCode);
        }

        AppDomain.CurrentDomain.FirstChanceException += Observe;

        try
        {
            Assert.False(await repository.RecordAlertAsync(90, 19m, 20m));
        }
        finally
        {
            observing.Value = false;

            AppDomain.CurrentDomain.FirstChanceException -= Observe;
        }

        Assert.Equal(1L, await CountAlertsAsync(90));

        Assert.Empty(logger.Warnings);

        Assert.Empty(constraintExceptions);

        await using DbCommand original = _db!.Database.GetDbConnection().CreateCommand();

        original.CommandText = "SELECT SpendUsd FROM BudgetAlerts WHERE Threshold=90";

        await _db.Database.OpenConnectionAsync();

        Assert.Equal(18m, Convert.ToDecimal(await original.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task Independent_connections_record_exactly_one_alert_when_their_prechecks_both_miss()
    {
        RequireSqlCipher();

        await using ArcanumDbContext other = _fixture.CreateContext(_dbPath);

        BudgetAlertRepository first = CreateRepository();

        BudgetAlertRepository second = new(other, NullLogger<BudgetAlertRepository>.Instance);

        Assert.False(await first.HasAlertedTodayAsync(80));

        Assert.False(await second.HasAlertedTodayAsync(80));

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> WriteAsync(BudgetAlertRepository repository) => Task.Run(async () =>
        {
            await start.Task;

            return await repository.RecordAlertAsync(80, 8m, 10m);
        });

        Task<bool>[] writes = [WriteAsync(first), WriteAsync(second)];

        start.SetResult();

        bool[] recorded = await Task.WhenAll(writes);

        Assert.Single(recorded, value => value);

        Assert.Equal(1L, await CountAlertsAsync(80));
    }

    [SkippableFact]
    public async Task A_previous_utc_day_does_not_suppress_todays_alert()
    {
        RequireSqlCipher();

        BudgetAlertRepository repository = CreateRepository();

        Assert.True(await repository.RecordAlertAsync(80, 8m, 10m));

        await using (DbCommand historical = _db!.Database.GetDbConnection().CreateCommand())
        {
            historical.CommandText = "UPDATE BudgetAlerts SET AlertedAt=@past WHERE Threshold=80";

            DbParameter parameter = historical.CreateParameter();

            parameter.ParameterName = "@past";

            parameter.Value = UtcInstantText.Format(DateTimeOffset.UtcNow.AddDays(-2));

            historical.Parameters.Add(parameter);

            _ = await historical.ExecuteNonQueryAsync();
        }

        Assert.False(await repository.HasAlertedTodayAsync(80));

        Assert.True(await repository.RecordAlertAsync(80, 9m, 10m));

        Assert.True(await repository.HasAlertedTodayAsync(80));

        Assert.Equal(2L, await CountAlertsAsync(80));
    }

    [SkippableFact]
    public async Task RecordAlertAsync_unrelated_unique_constraint_is_not_a_duplicate_threshold()
    {
        RequireSqlCipher();

        await _db!.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX test_budget_limit_unique ON BudgetAlerts(DailyLimitUsd)");

        BudgetAlertRepository repository = CreateRepository();

        Assert.True(await repository.RecordAlertAsync(80, 8m, 10m));

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(() => repository.RecordAlertAsync(100, 10m, 10m));

        Assert.Equal(2067, exception.SqliteExtendedErrorCode);

        Assert.Equal(0L, await CountAlertsAsync(100));
    }

    [SkippableFact]
    public async Task RecordAlertAsync_nonconstraint_database_error_propagates()
    {
        RequireSqlCipher();

        await _db!.Database.ExecuteSqlRawAsync("""DROP TABLE "BudgetAlerts";""");

        BudgetAlertRepository repository = CreateRepository();

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => repository.RecordAlertAsync(50, 5m, 10m));

        Assert.NotEqual(19, exception.SqliteErrorCode);

        Assert.Contains("BudgetAlerts", exception.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RecordAlertAsync_non_unique_constraint_failure_propagates_instead_of_reading_as_a_duplicate()
    {
        RequireSqlCipher();

        await _db!.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER "RejectBudgetAlertInsert"
            BEFORE INSERT ON "BudgetAlerts"
            BEGIN
                SELECT RAISE(ABORT, 'simulated non-unique constraint failure');
            END;
            """);

        CapturingLogger logger = new();

        BudgetAlertRepository repository = CreateRepository(logger);

        SqliteException exception = await Assert.ThrowsAsync<SqliteException>(
            () => repository.RecordAlertAsync(60, 6m, 10m));

        Assert.Equal(19, exception.SqliteErrorCode);

        Assert.Contains("simulated non-unique constraint failure", exception.Message, StringComparison.Ordinal);

        Assert.Empty(logger.Warnings);
    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

    private BudgetAlertRepository CreateRepository(ILogger<BudgetAlertRepository>? logger = null) =>
        new(_db!, logger ?? NullLogger<BudgetAlertRepository>.Instance);

    private async Task<long> CountAlertsAsync(int threshold)
    {
        await _db!.Database.CloseConnectionAsync();

        await using ArcanumDbContext verificationDb = _fixture.CreateContext(_dbPath);
        await using DbCommand command = verificationDb.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT COUNT(*)
            FROM "BudgetAlerts"
            WHERE "Threshold" = @threshold;
            """;

        DbParameter parameter = command.CreateParameter();

        parameter.ParameterName = "@threshold";

        parameter.Value = threshold;

        command.Parameters.Add(parameter);

        object? result = await command.ExecuteScalarAsync();

        return Assert.IsType<long>(result);
    }

    private sealed class CapturingLogger : ILogger<BudgetAlertRepository>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
