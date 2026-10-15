using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.LongRest;

public sealed partial class LongRestSweepStoreTests
{
    // A read that proves without changing anything has no business holding or waiting for the writer lock: it runs on every eligible Session
    // turn, whether or not anything is left to sweep, and a writer that is mid-commit elsewhere would otherwise stall the turn behind it.
    // The sibling connection here owns that lock for the whole of each test, so a probe that asks for it too would wait out its busy
    // timeout instead of answering, and the bound below turns that wait into a failure.
    private static readonly TimeSpan LockBound = TimeSpan.FromSeconds(20);

    private static async Task<SqliteTransaction> HoldWriterLockAsync(ArcanumDbContext sibling)
    {
        SqliteConnection connection = (SqliteConnection)sibling.Database.GetDbConnection();

        await sibling.Database.OpenConnectionAsync();

        return connection.BeginTransaction(deferred: false);
    }

    [Fact]
    public async Task Completed_replay_lookups_answer_while_another_connection_holds_the_writer_lock()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestSweep committed = LongRestSweepHarness.Require(await fixture.Store.CommitExactDuplicatesAsync(sweep, work.Identity, null, CancellationToken.None));

        await DrainCompletedCorpusAsync(fixture, committed);

        await fixture.H.ExecuteAsync("UPDATE LongRunningOperations SET State = $completed;", ("$completed", (int)RetroDownfall.Arcanum.Core.Operations.LongRunningOperationState.Completed));

        await using ArcanumDbContext sibling = fixture.H.Saga.CreateSiblingContext();

        await using SqliteTransaction held = await HoldWriterLockAsync(sibling);

        using CancellationTokenSource bound = new(LockBound);

        using SqliteStatementTrace trace = new(fixture.H.Connection);

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        CanonicalCampaignContext scope = fixture.Generation.Fence.CanonicalScope;

        Assert.Equal(fixture.Generation.OperationId, LongRestSweepHarness.Require(
            await fixture.Store.FindCompletedRequestReplayAsync(scope, fixture.Generation.Scope, fixture.Generation.Recipe, bound.Token)));

        Assert.Equal(fixture.Generation.OperationId, LongRestSweepHarness.Require(
            await fixture.Store.FindCompletedReplayAsync(scope, fixture.Generation.Scope, fixture.Generation.Recipe, bound.Token)));

        Assert.Equal(fixture.Generation.OperationId, LongRestSweepHarness.Require(
            await fixture.Store.FindCompletedReplayAsync(scope, fixture.Generation.Recipe, bound.Token)));

        Assert.Equal(3, tally.ReadTransactions);

        Assert.Equal(0, tally.WriterTransactions);

        Assert.Equal(0, trace.ImmediateTransactions);

        Assert.Equal(3, trace.DeferredTransactions);
    }

    [Fact]
    public async Task Attempt_validation_and_the_partition_probe_answer_while_another_connection_holds_the_writer_lock()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, duplicates: true);

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAttemptStore attempts = new(fixture.H.Saga.Context, TimeProvider.System);

        CovenantDigest payload = new(Enumerable.Repeat((byte)83, 32).ToArray());

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await attempts.PrepareAttemptAsync(work.Identity,
            LongRestProviderPayloadKind.Adjudication, payload, fixture.Generation.Recipe.SummaryDestinationDigest, CancellationToken.None));

        await using ArcanumDbContext sibling = fixture.H.Saga.CreateSiblingContext();

        await using SqliteTransaction held = await HoldWriterLockAsync(sibling);

        using CancellationTokenSource bound = new(LockBound);

        using SqliteStatementTrace trace = new(fixture.H.Connection);

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        Assert.True((await attempts.ValidateAttemptAsync(attempt, bound.Token)).IsSuccess);

        Assert.Null(LongRestSweepHarness.Require(await fixture.H.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, null, bound.Token)));

        Assert.Equal(1, tally.ReadTransactions);

        Assert.Equal(0, tally.WriterTransactions);

        Assert.Equal(0, trace.ImmediateTransactions);

        // The attempt store's one, and the discovery store's own for the partition probe.
        Assert.Equal(2, trace.DeferredTransactions);
    }

    [Fact]
    public async Task Writes_still_take_the_writer_lock_up_front()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, duplicates: true);

        using SqliteStatementTrace trace = new(fixture.H.Connection);

        using LongRestVerificationTally tally = LongRestVerificationCounters.Measure();

        LongRestSweep sweep = await fixture.BeginAsync();

        LongRestSweepWork work = LongRestSweepHarness.Require(await fixture.Store.ReadNextWorkAsync(sweep, null, CancellationToken.None))!;

        LongRestAttemptStore attempts = new(fixture.H.Saga.Context, TimeProvider.System);

        LongRestDispatchAttempt attempt = LongRestSweepHarness.Require(await attempts.PrepareAttemptAsync(work.Identity,
            LongRestProviderPayloadKind.Adjudication, new CovenantDigest(Enumerable.Repeat((byte)83, 32).ToArray()),
            fixture.Generation.Recipe.SummaryDestinationDigest, CancellationToken.None));

        Assert.NotNull(attempt);

        // Beginning a sweep, reading its next work (which queues it), and preparing an attempt each write, so each is an immediate transaction.
        Assert.Equal(3, tally.WriterTransactions);

        Assert.Equal(0, tally.ReadTransactions);

        Assert.Equal(3, trace.ImmediateTransactions);

        Assert.Equal(0, trace.DeferredTransactions);
    }
}
