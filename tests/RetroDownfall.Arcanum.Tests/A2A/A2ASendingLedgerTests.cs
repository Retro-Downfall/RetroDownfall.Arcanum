using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// The durable A2A task correspondence (issue #62), against the real <c>LongRunningOperations</c> ledger
/// rather than a stand-in — the whole point of the change is that the record outlives the process, which
/// only the real store can demonstrate.
/// </summary>
[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class A2ASendingLedgerTests : IAsyncLifetime
{

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public A2ASendingLedgerTests(GrimoireFixture fixture) => _fixture = fixture;

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
    public async Task RegisteredInboundSending_IsResolvableByTaskIdFromAFreshLedgerInstance()
    {

        RequireSqlCipher();

        Guid apprenticeId = Guid.NewGuid();

        A2ASendingLedgerEntry entry = await CreateLedger()
            .RegisterInboundAsync("task-abc", apprenticeId);

        Assert.True(entry.IsRecorded);

        // A fresh ledger stands in for the process that restarts: nothing in memory carries over, so a
        // resolvable answer here is the whole of what #62 restores.
        Assert.Equal(apprenticeId, await CreateLedger().FindInboundApprenticeAsync("task-abc"));

    }

    [SkippableFact]
    public async Task ReleasedSending_IsNoLongerResolvable()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterInboundAsync("task-done", Guid.NewGuid());

        await ledger.ReleaseAsync(entry);

        // A settled Sending must not be reconciled or cancelled after the next restart.
        Assert.Null(await CreateLedger().FindInboundApprenticeAsync("task-done"));

    }

    [SkippableFact]
    public async Task UnknownTaskId_ResolvesToNothingRatherThanThrowing()
    {

        RequireSqlCipher();

        Assert.Null(await CreateLedger().FindInboundApprenticeAsync("never-registered"));

        Assert.Null(await CreateLedger().FindInboundApprenticeAsync("   "));

    }

    [SkippableFact]
    public async Task RegisteredOutboundSending_CarriesTheAgentUrlNeededToCancelItLater()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterOutboundAsync("remote-9", "https://peer.example.test/");

        Assert.True(entry.IsRecorded);

        LongRunningOperationStore store = new(_db!);

        LongRunningOperation? operation = await store.GetAsync(entry.OperationId);

        Assert.NotNull(operation);

        A2ASendingRecord? record = A2ASendingLedger.TryRead(operation!);

        Assert.NotNull(record);

        // Without the URL a reconciler knows a remote task exists but has nowhere to send tasks/cancel.
        Assert.Equal("https://peer.example.test/", record!.AgentUrl);

        Assert.Equal("remote-9", record.TaskId);

        Assert.Equal(A2ASendingRecordDirection.Outbound, record.Direction);

    }

    [SkippableFact]
    public async Task BlankTaskId_IsNotRecordedAtAll()
    {

        RequireSqlCipher();

        Assert.False((await CreateLedger().RegisterInboundAsync("  ", Guid.NewGuid())).IsRecorded);

    }

    private IA2ASendingLedger CreateLedger() =>
        new A2ASendingLedger(
            new LongRunningOperationStore(_db!),
            TimeProvider.System,
            NullLogger<A2ASendingLedger>.Instance);

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

}
