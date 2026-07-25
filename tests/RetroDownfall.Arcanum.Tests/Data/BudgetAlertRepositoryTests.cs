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
    public async Task RecordAlertAsync_duplicate_threshold_today_returns_false_logs_and_keeps_one_row()
    {

        RequireSqlCipher();

        CapturingLogger logger = new();

        BudgetAlertRepository repository = CreateRepository(logger);

        Assert.True(await repository.RecordAlertAsync(90, 18m, 20m));

        Assert.False(await repository.RecordAlertAsync(90, 19m, 20m));

        Assert.Equal(1L, await CountAlertsAsync(90));

        string warning = Assert.Single(logger.Warnings);

        Assert.Contains("90", warning, StringComparison.Ordinal);

        Assert.Contains("already recorded today", warning, StringComparison.Ordinal);

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
