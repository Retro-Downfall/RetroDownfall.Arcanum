using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantAdmissionTests
{
    [Fact]
    public async Task CleanupCadenceIsStampedOnlyAfterTheAdmittedLeaseFinishesDisposing()
    {
        await using UnseenServantAdmissionHarness harness = new();

        UnseenServantAdmissionHarness.Checkpoint disposal = new();

        harness.OnStep = (step, _) => step == "lease-dispose" ? disposal.PauseAsync() : Task.CompletedTask;

        Task cleanup = harness.InvokeAsync("CleanupExpiredIdempotencyKeysAsync");

        try
        {
            await disposal.WaitAsync();

            Assert.Equal(DateTimeOffset.MinValue, harness.ReadField<DateTimeOffset>("_lastIdempotencyCleanupUtc"));
        }
        finally
        {
            disposal.Release.TrySetResult();

            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(harness.Clock.GetUtcNow(), harness.ReadField<DateTimeOffset>("_lastIdempotencyCleanupUtc"));
    }


    [Fact]
    public async Task DeferredStartupHydrationRetriesBeforeTheFirstDispatch()
    {
        await using UnseenServantAdmissionHarness harness = new();

        harness.Settings.Daemon.Jobs = [new UnseenServantJob { Name = "watch", TargetSpell = "patrol", IntervalMinutes = 5 }];

        harness.Store.Rows.Add(new UnseenServantWatermark("watch\0patrol", harness.Clock.GetUtcNow().AddHours(-2), 5));

        IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

        TaskCompletionSource<string> firstWork = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.OnStep = (step, _) =>
        {
            if (step == "hydrate" || step.StartsWith("runner:", StringComparison.Ordinal))
            {
                firstWork.TrySetResult(step);
            }

            return Task.CompletedTask;
        };

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            await harness.Clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(harness.Events);

            Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

            IGrimoireExclusiveClosedLease closed = (await harness.Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

            await harness.Clock.TickAsync();

            Assert.Equal("hydrate", await firstWork.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task OwnedClockTickDispatchesAnAlreadyHydratedDueJob()
    {
        await using UnseenServantAdmissionHarness harness = new();

        harness.Settings.Daemon.Jobs = [new UnseenServantJob { Name = "watch", TargetSpell = "patrol", IntervalMinutes = 5 }];

        harness.Store.Rows.Add(new UnseenServantWatermark("watch\0patrol", harness.Clock.GetUtcNow().AddHours(-2), 5));

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        harness.OnStep = (step, token) => step.StartsWith("runner:", StringComparison.Ordinal) ? runner.PauseAsync(token) : Task.CompletedTask;

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            await harness.Clock.TickAsync();

            await runner.WaitAsync();

            Assert.Single(harness.Events, static step => step.StartsWith("runner:", StringComparison.Ordinal));

            Assert.Contains("hydrate", harness.Events);
        }
        finally
        {
            runner.Release.TrySetResult();

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("HydrateWatermarksAsync")]
    [InlineData("CleanupExpiredIdempotencyKeysAsync")]
    public async Task DeniedDbOnlyUnitCreatesNoScopeOrFallbackWarning(string operation)
    {
        await using UnseenServantAdmissionHarness harness = new();

        Assert.True(harness.Inner.BeginOrResumeExclusive(Owner()).IsSuccess);

        await harness.InvokeAsync(operation);

        Assert.Equal(0, harness.Scopes.Created);

        Assert.Empty(harness.Events);

        Assert.DoesNotContain(harness.Logger.Entries, static entry => entry.Level >= LogLevel.Warning);

        Assert.Equal([GrimoireWorkKind.UnseenServant], harness.Gate.RequestedWorkKinds);

        Assert.Equal(DateTimeOffset.MinValue, harness.ReadField<DateTimeOffset>("_lastIdempotencyCleanupUtc"));
    }

    [Theory]
    [InlineData("HydrateWatermarksAsync", "hydrate")]
    [InlineData("CleanupExpiredIdempotencyKeysAsync", "legacy-cleanup")]
    public async Task DbOnlyHostCancellationIsNotAFallbackWarning(string operation, string stage)
    {
        await using UnseenServantAdmissionHarness harness = new();

        using CancellationTokenSource cancellation = new();

        harness.OnStep = (step, token) =>
        {
            if (step == stage)
            {
                cancellation.Cancel();

                token.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.InvokeAsync(operation, cancellation.Token));

        Assert.DoesNotContain(harness.Logger.Entries, static entry => entry.Level >= LogLevel.Warning);

        Assert.Equal(DateTimeOffset.MinValue, harness.ReadField<DateTimeOffset>("_lastIdempotencyCleanupUtc"));
    }

    [Theory]
    [InlineData("HydrateWatermarksAsync", "hydrate")]
    [InlineData("CleanupExpiredIdempotencyKeysAsync", "claim-cleanup")]
    public async Task DbOnlyUnitDrainsThroughAsyncScopeDisposalWithoutAGroup(string operation, string stage)
    {
        await using UnseenServantAdmissionHarness harness = new();

        UnseenServantAdmissionHarness.Checkpoint work = new();

        UnseenServantAdmissionHarness.Checkpoint disposal = new();

        harness.OnStep = (step, _) => step == stage
            ? work.PauseAsync()
            : step == "scope-dispose" ? disposal.PauseAsync() : Task.CompletedTask;

        Task running = harness.InvokeAsync(operation);

        try
        {
            await work.WaitAsync();

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            Task<Result> drain = harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            Assert.Equal(0, harness.Gate.EffectGroupAttempts);

            work.Release.TrySetResult();

            await disposal.WaitAsync();

            Assert.False(drain.IsCompleted);

            disposal.Release.TrySetResult();

            await running;

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Equal(["scope-dispose", "lease-dispose"], harness.Events.Where(static step => step.EndsWith("-dispose", StringComparison.Ordinal)));
        }
        finally
        {
            work.Release.TrySetResult();

            disposal.Release.TrySetResult();

            await running;
        }
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));
}
