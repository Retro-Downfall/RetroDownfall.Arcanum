using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

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

    // ── #68: a Sending parked awaiting the peer's answer ───────────────────────────────────────────

    [SkippableFact]
    public async Task ParkedInboundSending_IsResolvableAsParkedFromAFreshLedgerInstance()
    {

        RequireSqlCipher();

        Guid apprenticeId = Guid.NewGuid();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterInboundAsync("task-parked", apprenticeId);

        await ledger.MarkParkedAsync(entry, "ctx-7");

        // The fresh ledger stands in for the restart: the escalated Apprentice was always durable, this
        // is the correspondence that tells a peer's answer which Apprentice asked the question.
        A2AParkedSending? parked = await CreateLedger().FindParkedInboundAsync("task-parked");

        Assert.NotNull(parked);

        Assert.Equal(apprenticeId, parked!.Value.ApprenticeId);

        Assert.Equal("ctx-7", parked.Value.ContextId);

    }

    [SkippableFact]
    public async Task UnparkedInboundSending_IsNotResolvableAsParked()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        await ledger.RegisterInboundAsync("task-working", Guid.NewGuid());

        // A Sending still being worked is not answerable; only an escalated one is.
        Assert.Null(await CreateLedger().FindParkedInboundAsync("task-working"));

    }

    [SkippableFact]
    public async Task ParkedSendingFlaggedByReconciliation_IsStillResolvableAndCanStillBeClosed()
    {

        RequireSqlCipher();

        Guid apprenticeId = Guid.NewGuid();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterInboundAsync("task-flagged", apprenticeId);

        await ledger.MarkParkedAsync(entry, "ctx-8");

        LongRunningOperationStore store = new(_db!);

        LongRunningOperation flagged = (await store.GetAsync(entry.OperationId))!;

        // Exactly what startup reconciliation does to a parked Sending: flag it, do not close it.
        Assert.True(await store.TryTransitionAsync(
            entry.OperationId,
            flagged.Revision,
            ownerId: null,
            LongRunningOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            A2ASendingRecoveryOutcomes.InboundParkedAwaitingAnswer));

        IA2ASendingLedger afterRestart = CreateLedger();

        A2AParkedSending? parked = await afterRestart.FindParkedInboundAsync("task-flagged");

        Assert.NotNull(parked);

        Assert.Equal(apprenticeId, parked!.Value.ApprenticeId);

        // Flagged, not terminal: this process takes the lease, resumes the Apprentice, and closes the
        // record when the Sending finally settles.
        Assert.True(parked.Value.Ledger.IsRecorded);

        await afterRestart.ReleaseAsync(parked.Value.Ledger);

        Assert.Null(await CreateLedger().FindParkedInboundAsync("task-flagged"));

    }

    [SkippableFact]
    public async Task ParkedSendingWhoseLeaseReconciliationReleased_IsStillClosedWhenItSettles()
    {

        RequireSqlCipher();

        IA2ASendingLedger ledger = CreateLedger();

        A2ASendingLedgerEntry entry = await ledger.RegisterInboundAsync("task-stale-lease", Guid.NewGuid());

        await ledger.MarkParkedAsync(entry, "ctx-9");

        LongRunningOperationStore store = new(_db!);

        LongRunningOperation flagged = (await store.GetAsync(entry.OperationId))!;

        // Nothing heartbeats a Sending that is waiting on a peer, so reconciliation eventually flags it
        // and drops its lease. A handle taken before that still has to be able to close the record, or a
        // settled Sending is left open and re-examined on every pass.
        Assert.True(await store.TryTransitionAsync(
            entry.OperationId,
            flagged.Revision,
            ownerId: null,
            LongRunningOperationState.ReconciliationRequired,
            DateTimeOffset.UtcNow,
            A2ASendingRecoveryOutcomes.InboundParkedAwaitingAnswer));

        Assert.Null((await store.GetAsync(entry.OperationId))!.LeaseOwner);

        await ledger.ReleaseAsync(entry);

        Assert.Equal(
            LongRunningOperationState.Completed,
            (await store.GetAsync(entry.OperationId))!.State);

        Assert.Null(await CreateLedger().FindInboundApprenticeAsync("task-stale-lease"));

    }

    // ── the durable lease on a Sending that is still running ───────────────────────────────────────

    [SkippableFact]
    public async Task SendingStillInFlight_KeepsItsLeaseSoBackgroundReconciliationCannotReclaimIt()
    {

        RequireSqlCipher();

        FakeTimeProvider clock = new();

        A2ASendingLeaseRenewer renewer = CreateRenewer(clock);

        A2ASendingLedgerEntry entry = await CreateLedger(clock, renewer)
            .RegisterOutboundAsync("remote-slow", "https://peer.example.test/");

        // One renewal pass, well inside the lease the registration took.
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, await renewer.RenewHeldAsync(CancellationToken.None));

        // Eighteen minutes in, the peer is still working and this process is still awaiting it. Unrenewed,
        // the row is "expired" here, so the reconciliation pass that runs every minute re-leases it and the
        // outbound recovery handler sends tasks/cancel for the very task the dispatch is waiting on.
        clock.Advance(TimeSpan.FromMinutes(13));

        IReadOnlyList<LongRunningOperation> expired = await new LongRunningOperationStore(_db!)
            .FindExpiredAsync(clock.GetUtcNow(), 100);

        Assert.DoesNotContain(expired, operation => operation.Id == entry.OperationId);

    }

    [SkippableFact]
    public async Task SettledSending_IsNoLongerRenewed()
    {

        RequireSqlCipher();

        FakeTimeProvider clock = new();

        A2ASendingLeaseRenewer renewer = CreateRenewer(clock);

        IA2ASendingLedger ledger = CreateLedger(clock, renewer);

        A2ASendingLedgerEntry entry = await ledger.RegisterInboundAsync("task-settled", Guid.NewGuid());

        await ledger.ReleaseAsync(entry);

        clock.Advance(TimeSpan.FromMinutes(5));

        // Renewing a closed row forever would keep a settled Sending out of every later pass.
        Assert.Equal(0, await renewer.RenewHeldAsync(CancellationToken.None));

    }

    [SkippableFact]
    public async Task ParkedSending_IsNoLongerRenewed()
    {

        RequireSqlCipher();

        FakeTimeProvider clock = new();

        A2ASendingLeaseRenewer renewer = CreateRenewer(clock);

        IA2ASendingLedger ledger = CreateLedger(clock, renewer);

        A2ASendingLedgerEntry entry = await ledger.RegisterInboundAsync("task-parked-lease", Guid.NewGuid());

        await ledger.MarkParkedAsync(entry, "ctx-11");

        clock.Advance(TimeSpan.FromMinutes(5));

        // Waiting on a peer is not work: reconciliation is supposed to flag a parked Sending, which
        // renewing its lease forever would prevent (§5.7.1.5).
        Assert.Equal(0, await renewer.RenewHeldAsync(CancellationToken.None));

    }

    private IA2ASendingLedger CreateLedger() =>
        new A2ASendingLedger(
            new LongRunningOperationStore(_db!),
            TimeProvider.System,
            NullLogger<A2ASendingLedger>.Instance);

    private IA2ASendingLedger CreateLedger(TimeProvider clock, A2ASendingLeaseRenewer renewer) =>
        new A2ASendingLedger(
            new LongRunningOperationStore(_db!),
            clock,
            NullLogger<A2ASendingLedger>.Instance,
            renewer);

    private A2ASendingLeaseRenewer CreateRenewer(TimeProvider clock)
    {

        ServiceCollection services = new();

        services.AddScoped<ILongRunningOperationStore>(_ => new LongRunningOperationStore(_db!));

        return new A2ASendingLeaseRenewer(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            clock,
            NullLogger<A2ASendingLeaseRenewer>.Instance);

    }

    private static void RequireSqlCipher() =>
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

}
