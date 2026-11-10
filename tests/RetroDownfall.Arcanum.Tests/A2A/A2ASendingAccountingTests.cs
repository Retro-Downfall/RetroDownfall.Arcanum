using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Operations;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Issue #69: what a delegated Sending cost stops being merely legible and becomes accounted. Against
/// the real ledger, because "an operator with a spend ceiling can blow through it by delegating" is a
/// claim about durable rows, not about an in-memory total.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class A2ASendingAccountingTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public A2ASendingAccountingTests(GrimoireFixture fixture) => _fixture = fixture;

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
    public async Task SettledSending_WithKnownCost_RecordsThatCostOnItsDurableRow()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterOutboundAsync("remote-1", "https://peer.example.test/");

        await ledger.SettleOutboundAsync(entry, A2ARemoteCost.Known(1_500, 0.42m));

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation operation = (await store.GetAsync(entry.OperationId))!;

        A2ASendingRecord record = A2ASendingLedger.TryRead(operation)!;

        Assert.True(record.CostKnown);

        Assert.Equal(0.42m, record.CostUsd);

        Assert.Equal(1_500, record.TotalTokens);

        Assert.Equal(LongRunningOperationState.Completed, operation.State);

    }

    [SkippableFact]
    public async Task SettledSending_WithUnknownCost_IsRecordedAsUnpricedRatherThanZero()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterOutboundAsync("remote-2", "https://peer.example.test/");

        await ledger.SettleOutboundAsync(entry, A2ARemoteCost.Unknown);

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        A2ASendingRecord record = A2ASendingLedger.TryRead((await store.GetAsync(entry.OperationId))!)!;

        // A zero here would read as "this delegated inference was free", which is the exact confusion
        // #60 removed from the reporting surfaces and #69 must not reintroduce in the accounting ones.
        Assert.False(record.CostKnown);

        Assert.Null(record.CostUsd);

    }

    [SkippableFact]
    public async Task ExternalSpendLedger_SumsKnownCostOnceAndCountsUnpricedSendingsSeparately()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        await SettleAsync(ledger, "remote-a", A2ARemoteCost.Known(100, 1.25m));

        await SettleAsync(ledger, "remote-b", A2ARemoteCost.Known(200, 0.75m));

        await SettleAsync(ledger, "remote-c", A2ARemoteCost.Unknown);

        ExternalSpendSummary summary = await CreateExternalSpendLedger().GetTodayAsync();

        Assert.Equal(2.00m, summary.KnownCostUsd);

        Assert.Equal(300, summary.KnownTokens);

        Assert.Equal(2, summary.PricedSendings);

        // "Unpriced delegated work exists" is itself the signal; it is never folded into the total.
        Assert.Equal(1, summary.UnpricedSendings);

        Assert.True(summary.HasUnpricedDelegation);

    }

    [SkippableTheory]
    [InlineData("precision")]
    [InlineData("range")]
    public async Task ExternalSpendLedger_LegacyTotalsRefuseAnyUnrepresentableUsdSum(string boundary)
    {
        RequireSqlCipher();

        await SeedLegacyExternalSpendBoundaryAsync(boundary);

        await Assert.ThrowsAsync<OverflowException>(() => CreateExternalSpendLedger().GetTodayAsync());
    }

    [SkippableFact]
    public async Task ExternalSpendLedger_LegacyTokenOverflowNeverWrapsIntoAnApparentTotal()
    {
        RequireSqlCipher();

        await SeedLegacyExternalSpendBoundaryAsync("tokens");

        await Assert.ThrowsAsync<OverflowException>(() => CreateExternalSpendLedger().GetTodayAsync());
    }

    [SkippableFact]
    public async Task ExternalSpendLedger_LegacyRepresentableSubcentTotalRemainsExact()
    {
        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        await SettleAsync(ledger, "tiny-a", A2ARemoteCost.Known(7, 0.00000001m));

        await SettleAsync(ledger, "tiny-b", A2ARemoteCost.Known(8, 0.00000002m));

        ExternalSpendSummary actual = await CreateExternalSpendLedger().GetTodayAsync();

        Assert.Equal(new ExternalSpendSummary(0.00000003m, 15, 2, 0), actual);
    }

    [SkippableFact]
    public async Task ExternalSpendLedger_StreamsEverySettledLegacyRowBeyondTheOldScanCeiling()
    {
        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        var entry = await ledger.RegisterOutboundAsync("bulk-template", "https://peer.example.test/");

        Assert.True(entry.IsRecorded);

        await ledger.SettleOutboundAsync(entry, A2ARemoteCost.Known(1, 0.00000001m));

        LongRunningOperationStore operations = new(_db!, TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation template = (await operations.GetAsync(entry.OperationId))!;

        Assert.Equal(LongRunningOperationState.Completed, template.State);

        A2ASendingRecord payload = A2ASendingLedger.TryRead(template)!;

        Assert.True(payload.CostKnown);

        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open) await _db.Database.OpenConnectionAsync();

        await using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            await using SqliteCommand insert = connection.CreateCommand();

            insert.Transaction = transaction;

            insert.CommandText = """
                INSERT INTO LongRunningOperations(Id,Kind,State,RecoveryPolicy,CreatedAt,StartedAt,HeartbeatAt,
                    CompletedAt,AttemptCount,CheckpointVersion,CheckpointPayload,PublicSummary,Revision)
                SELECT $id,Kind,State,RecoveryPolicy,CreatedAt,StartedAt,HeartbeatAt,CompletedAt,
                    AttemptCount,CheckpointVersion,$payload,$summary,Revision
                FROM LongRunningOperations WHERE lower(replace(Id,'-',''))=$template
                """;

            insert.Parameters.AddWithValue("$template", entry.OperationId.ToString("N"));

            SqliteParameter identity = insert.Parameters.AddWithValue("$id", "");

            SqliteParameter checkpoint = insert.Parameters.AddWithValue("$payload", Array.Empty<byte>());

            SqliteParameter summary = insert.Parameters.AddWithValue("$summary", "");

            // One transaction, unique operation/task identities and an actual settled row shape:
            // 10,000 tiny sources followed by a distinct late source that a capped scan cannot drop.
            for (int index = 0; index < 10_000; index++)
            {
                bool late = index == 9_999;

                string task = $"bulk-{index}";

                identity.Value = Guid.NewGuid().ToString("N");

                checkpoint.Value = JsonSerializer.SerializeToUtf8Bytes(payload with
                {
                    TaskId = task,
                    CostUsd = late ? 1m : 0.00000001m,
                    TotalTokens = late ? 7 : 1,
                }, A2ASendingLedgerJsonContext.Default.A2ASendingRecord);

                summary.Value = $"Outbound A2A Sending {task} settled.";

                Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            }

            await transaction.CommitAsync();
        }

        Assert.Equal(new ExternalSpendSummary(1.0001m, 10_007, 10_001, 0),
            await CreateExternalSpendLedger().GetTodayAsync());
    }

    [SkippableFact]
    public async Task ExternalSpendLedger_RequiredTableReadFailureNeverBecomesNoSpend()
    {
        RequireSqlCipher();

        await DropExternalSpendTableAsync();

        await Assert.ThrowsAsync<SqliteException>(() => CreateExternalSpendLedger().GetTodayAsync());
    }

    [SkippableTheory]
    [InlineData("precision")]
    [InlineData("range")]
    [InlineData("tokens")]
    [InlineData("storage")]
    public async Task ExternalSpendBudgetMonitor_PropagatesUnverifiableSpendBeforeSuccessfulAdmission(string boundary)
    {
        RequireSqlCipher();

        if (boundary == "storage") await DropExternalSpendTableAsync();
        else await SeedLegacyExternalSpendBoundaryAsync(boundary);

        ArcanumSettings settings = new() { Cost = new() { Budget = new() { Enabled = true, DailyLimitUsd = 10m } } };

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        ServiceCollection services = new();

        services.AddScoped<IBudgetReservationService>(_ => new BudgetReservationService(_db!, options));

        services.AddSingleton<IBudgetAlertRepository>(alerts);

        services.AddSingleton(CreateExternalSpendLedger());

        await using ServiceProvider provider = services.BuildServiceProvider();

        BudgetMonitor monitor = new(provider.GetRequiredService<IServiceScopeFactory>(), commLink, options, NullLogger<BudgetMonitor>.Instance);

        if (boundary == "storage") await Assert.ThrowsAsync<SqliteException>(() => monitor.CheckAsync());
        else await Assert.ThrowsAsync<OverflowException>(() => monitor.CheckAsync());

        Assert.Equal(0, alerts.PrecheckCalls);

        Assert.Equal(0, alerts.RecordCalls);

        Assert.Empty(commLink.Dispatched);
    }

    [SkippableFact]
    public async Task ExternalSpendBudgetMonitor_NoRegisteredLedgerKeepsTheLegitimateEmptyFallback()
    {
        RequireSqlCipher();

        ArcanumSettings settings = new() { Cost = new() { Budget = new() { Enabled = true, DailyLimitUsd = 10m } } };

        TestOptionsMonitor<ArcanumSettings> options = new(settings);

        FakeBudgetAlertRepository alerts = new();

        FakeCommLinkDispatcher commLink = new();

        ServiceCollection services = new();

        services.AddScoped<IBudgetReservationService>(_ => new BudgetReservationService(_db!, options));

        services.AddSingleton<IBudgetAlertRepository>(alerts);

        await using ServiceProvider provider = services.BuildServiceProvider();

        BudgetMonitor monitor = new(provider.GetRequiredService<IServiceScopeFactory>(), commLink, options, NullLogger<BudgetMonitor>.Instance);

        Assert.True((await monitor.CheckAsync()).IsSuccess);

        Assert.Equal(0, alerts.RecordCalls);

        Assert.Empty(commLink.Dispatched);
    }

    private async Task SeedLegacyExternalSpendBoundaryAsync(string boundary)
    {
        IA2ASendingLedger ledger = CreateLedger();

        if (boundary == "tokens")
        {
            await SettleAsync(ledger, "tokens-a", A2ARemoteCost.Known(long.MaxValue, 0m));

            await SettleAsync(ledger, "tokens-b", A2ARemoteCost.Known(1, 0m));
        }
        else
        {
            await SettleAsync(ledger, "usd-a", A2ARemoteCost.Known(0, decimal.MaxValue));

            await SettleAsync(ledger, "usd-b", A2ARemoteCost.Known(0, boundary == "precision" ? 0.00000001m : 1m));
        }
    }

    private async Task DropExternalSpendTableAsync()
    {
        await _db!.Database.OpenConnectionAsync();

        await using var command = _db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "DROP TABLE LongRunningOperations";

        await command.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task ExternalSpendLedger_IgnoresSendingsThatSettledOnAnEarlierDay()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry yesterday = await ledger.RegisterOutboundAsync("remote-old", "https://peer.example.test/");

        await ledger.SettleOutboundAsync(yesterday, A2ARemoteCost.Known(10, 5m));

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        LongRunningOperation settled = (await store.GetAsync(yesterday.OperationId))!;

        // Daily spend is a daily figure; a Sending that settled before midnight is last night's money.
        Assert.True(await store.TryTransitionAsync(
            yesterday.OperationId,
            settled.Revision,
            ownerId: null,
            LongRunningOperationState.Completed,
            DateTimeOffset.UtcNow.AddDays(-2)));

        ExternalSpendSummary summary = await CreateExternalSpendLedger().GetTodayAsync();

        Assert.Equal(0m, summary.KnownCostUsd);

        Assert.Equal(0, summary.PricedSendings);

        Assert.Equal(0, summary.UnpricedSendings);

    }

    [SkippableFact]
    public async Task ExternalSpendLedger_IgnoresSendingsStillInFlight()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        await ledger.RegisterOutboundAsync("remote-inflight", "https://peer.example.test/");

        // An in-flight Sending has no cost yet; counting it as unpriced would raise a permanent flag for
        // work that simply has not finished.
        ExternalSpendSummary summary = await CreateExternalSpendLedger().GetTodayAsync();

        Assert.Equal(0, summary.PricedSendings);

        Assert.Equal(0, summary.UnpricedSendings);

    }

    [SkippableFact]
    public async Task OutboundSending_LinksItsLedgerRowToTheCallersBudgetReservation()
    {

        RequireSqlCipher();

        Guid reservationId = Guid.NewGuid();

        A2ASendingLedgerEntry entry = await CreateLedger()
            .RegisterOutboundAsync("remote-linked", "https://peer.example.test/", reservationId);

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        // Without the link, an operator looking at a reservation cannot see the delegated work it paid for.
        Assert.Equal(reservationId, (await store.GetAsync(entry.OperationId))!.BudgetReservationId);

    }

    [SkippableFact]
    public async Task OutboundSending_WithoutACallerReservation_IsStillRecorded()
    {

        RequireSqlCipher();

        A2ASendingLedgerEntry entry = await CreateLedger()
            .RegisterOutboundAsync("remote-unlinked", "https://peer.example.test/");

        LongRunningOperationStore store = new(
            _db!,
            TestOrdinaryConnectionFactory.For(_db!));

        Assert.Null((await store.GetAsync(entry.OperationId))!.BudgetReservationId);

    }

    // ── #67: a callback that arrives after the dispatching process is gone ─────────────────────────

    [SkippableFact]
    public async Task RecordedCallback_IsResolvableFromAFreshLedgerAndSettlesTheSending()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterOutboundAsync("remote-cb", "https://peer.example.test/");

        string token = A2ACallbackToken.Mint();

        // The id a peer is actually handed, minted the one way a callback config id is ever minted.
        string configId = A2ACallbackConfigId.Mint();

        await ledger.RecordOutboundCallbackAsync(entry, configId, A2ACallbackToken.Hash(token));

        // The fresh ledger is the restart: the awaiting process is gone and only the record remains.
        IA2ASendingLedger afterRestart = CreateLedger();

        A2AOutboundCallback? recovered = await afterRestart.FindOutboundCallbackAsync(configId);

        Assert.NotNull(recovered);

        Assert.Equal("remote-cb", recovered!.Value.TaskId);

        // The secret is never persisted — only its digest — so reading the Grimoire cannot hand anyone a
        // working callback credential.
        Assert.NotEqual(token, recovered.Value.TokenHash);

        Assert.True(A2ACallbackToken.Matches(token, recovered.Value.TokenHash));

        Assert.False(A2ACallbackToken.Matches(A2ACallbackToken.Mint(), recovered.Value.TokenHash));

        await afterRestart.SettleOutboundAsync(recovered.Value.Ledger, A2ARemoteCost.Unknown);

        // Settled, so a later callback for the same config finds nothing to settle twice.
        Assert.Null(await CreateLedger().FindOutboundCallbackAsync(configId));

    }

    [SkippableFact]
    public async Task UnknownCallbackConfig_ResolvesToNothingRatherThanThrowing()
    {

        RequireSqlCipher();

        Assert.Null(await CreateLedger().FindOutboundCallbackAsync(A2ACallbackConfigId.Mint()));

        Assert.Null(await CreateLedger().FindOutboundCallbackAsync("   "));

    }

    [Fact]
    public async Task CallbackConfigIdNothingCouldHaveMinted_IsRefusedWithoutScanningTheLedger()
    {

        FakeLongRunningOperationStore store = new(TimeProvider.System);

        IA2ASendingLedger ledger = new A2ASendingLedger(
            store,
            TimeProvider.System,
            NullLogger<A2ASendingLedger>.Instance);

        // POST {ServerPath}/callbacks/{configId} is the one deliberately anonymous A2A route (§5.7.1.4),
        // so {configId} is an unauthenticated peer-supplied string. An id outside the shape this
        // instance mints must be answered from its shape alone, not by paging every outbound Sending
        // the retention window still holds.
        Assert.Null(await ledger.FindOutboundCallbackAsync("never-registered"));

        Assert.Null(await ledger.FindOutboundCallbackAsync(new string('a', 4096)));

        Assert.Equal(0, store.ListCallCount);

    }

    private static async Task SettleAsync(IA2ASendingLedger ledger, string taskId, A2ARemoteCost cost)
    {

        A2ASendingLedgerEntry entry = await ledger.RegisterOutboundAsync(taskId, "https://peer.example.test/");

        await ledger.SettleOutboundAsync(entry, cost);

    }

    private IA2ASendingLedger CreateLedger() =>
        new A2ASendingLedger(
            new LongRunningOperationStore(
                _db!,
                TestOrdinaryConnectionFactory.For(_db!)),
            TimeProvider.System,
            NullLogger<A2ASendingLedger>.Instance);

    private IExternalSpendLedger CreateExternalSpendLedger() =>
        new A2AExternalSpendLedger(
            _db!,
            TimeProvider.System,
            NullLogger<A2AExternalSpendLedger>.Instance);

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

}
