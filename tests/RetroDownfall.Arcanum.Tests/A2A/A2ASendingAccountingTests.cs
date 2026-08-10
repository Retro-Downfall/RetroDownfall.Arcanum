using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

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

        LongRunningOperationStore store = new(_db!);

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

        LongRunningOperationStore store = new(_db!);

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

    [SkippableFact]
    public async Task ExternalSpendLedger_IgnoresSendingsThatSettledOnAnEarlierDay()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry yesterday = await ledger.RegisterOutboundAsync("remote-old", "https://peer.example.test/");

        await ledger.SettleOutboundAsync(yesterday, A2ARemoteCost.Known(10, 5m));

        LongRunningOperationStore store = new(_db!);

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

        LongRunningOperationStore store = new(_db!);

        // Without the link, an operator looking at a reservation cannot see the delegated work it paid for.
        Assert.Equal(reservationId, (await store.GetAsync(entry.OperationId))!.BudgetReservationId);

    }

    [SkippableFact]
    public async Task OutboundSending_WithoutACallerReservation_IsStillRecorded()
    {

        RequireSqlCipher();

        A2ASendingLedgerEntry entry = await CreateLedger()
            .RegisterOutboundAsync("remote-unlinked", "https://peer.example.test/");

        LongRunningOperationStore store = new(_db!);

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

        await ledger.RecordOutboundCallbackAsync(entry, "config-77", A2ACallbackToken.Hash(token));

        // The fresh ledger is the restart: the awaiting process is gone and only the record remains.
        IA2ASendingLedger afterRestart = CreateLedger();

        A2AOutboundCallback? recovered = await afterRestart.FindOutboundCallbackAsync("config-77");

        Assert.NotNull(recovered);

        Assert.Equal("remote-cb", recovered!.Value.TaskId);

        // The secret is never persisted — only its digest — so reading the Grimoire cannot hand anyone a
        // working callback credential.
        Assert.NotEqual(token, recovered.Value.TokenHash);

        Assert.True(A2ACallbackToken.Matches(token, recovered.Value.TokenHash));

        Assert.False(A2ACallbackToken.Matches(A2ACallbackToken.Mint(), recovered.Value.TokenHash));

        await afterRestart.SettleOutboundAsync(recovered.Value.Ledger, A2ARemoteCost.Unknown);

        // Settled, so a later callback for the same config finds nothing to settle twice.
        Assert.Null(await CreateLedger().FindOutboundCallbackAsync("config-77"));

    }

    [SkippableFact]
    public async Task UnknownCallbackConfig_ResolvesToNothingRatherThanThrowing()
    {

        RequireSqlCipher();

        Assert.Null(await CreateLedger().FindOutboundCallbackAsync("never-registered"));

        Assert.Null(await CreateLedger().FindOutboundCallbackAsync("   "));

    }

    private static async Task SettleAsync(IA2ASendingLedger ledger, string taskId, A2ARemoteCost cost)
    {

        A2ASendingLedgerEntry entry = await ledger.RegisterOutboundAsync(taskId, "https://peer.example.test/");

        await ledger.SettleOutboundAsync(entry, cost);

    }

    private IA2ASendingLedger CreateLedger() =>
        new A2ASendingLedger(
            new LongRunningOperationStore(_db!),
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
