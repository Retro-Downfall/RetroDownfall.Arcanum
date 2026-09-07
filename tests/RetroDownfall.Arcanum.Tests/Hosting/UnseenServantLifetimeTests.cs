using Microsoft.Extensions.Logging;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantLifetimeTests
{
    [Fact]
    public async Task HostCancellationDuringWatermarkIsNotReportedAsPersistenceFailure()
    {
        await using UnseenServantAdmissionHarness harness = new();

        await harness.ConfigureDueJobAsync();

        using CancellationTokenSource host = new();

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        harness.OnStep = (step, token) =>
        {
            if (step == "watermark")
            {
                Assert.Equal(host.Token, token);

                host.Cancel();

                token.ThrowIfCancellationRequested();
            }

            return step.StartsWith("runner:", StringComparison.Ordinal) ? runner.PauseAsync() : Task.CompletedTask;
        };

        harness.Dispatch(host.Token);

        Task owned = Assert.Single(harness.ActiveTasks);

        await runner.WaitAsync();

        runner.Release.TrySetResult();

        await owned.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain(harness.Logger.Entries, static entry => entry.Level >= LogLevel.Warning);

        Assert.Contains(harness.Logger.Entries, static entry => entry.Message.Contains("cancelled during shutdown", StringComparison.Ordinal));

        Assert.Equal(["scope-dispose", "group-dispose", "lease-dispose"], harness.Events.Where(static step => step.EndsWith("-dispose", StringComparison.Ordinal)));

        Assert.Empty(harness.Store.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonHostRunnerExceptionsAreContainedAndReleaseTheOwnedSlot(bool cancellationException)
    {
        await using UnseenServantAdmissionHarness harness = new();

        await harness.ConfigureDueJobAsync();

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        harness.OnStep = async (step, _) =>
        {
            if (step.StartsWith("runner:", StringComparison.Ordinal))
            {
                await runner.PauseAsync();

                throw cancellationException ? new OperationCanceledException("provider cancellation") : new IOException("provider failure");
            }
        };

        harness.Dispatch();

        Task owned = Assert.Single(harness.ActiveTasks);

        await runner.WaitAsync();

        runner.Release.TrySetResult();

        await owned.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(harness.Logger.Entries, static entry => entry.Level == LogLevel.Error);

        Assert.Empty(harness.RunningKeys);

        Assert.Empty(harness.ActiveTasks);

        Assert.Empty(harness.Admission.Waits);

        Assert.Equal(["group-dispose", "lease-dispose"], harness.Events.Where(static step => step.EndsWith("-dispose", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task KeepClosedRetainsOnlyConfiguredSlotsAndStopJoinsEveryWaiter(int capacity)
    {
        await using UnseenServantAdmissionHarness harness = new();

        harness.Settings.Daemon.MaxConcurrentJobs = capacity;

        harness.Settings.Daemon.Jobs = Enumerable.Range(0, capacity * 10)
            .Select(index => new UnseenServantJob { Name = $"watch-{index}", TargetSpell = "patrol", IntervalMinutes = 5 }).ToList();

        foreach (UnseenServantJob job in harness.Settings.Daemon.Jobs)
        {
            harness.Store.Rows.Add(new UnseenServantWatermark($"{job.Name}\0patrol", harness.Clock.GetUtcNow().AddHours(-2), 5));
        }

        await harness.Service.StartAsync(CancellationToken.None);

        try
        {
            await harness.Clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));

            int startupScopes = harness.Scopes.Created;

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

            harness.OnStep = (step, _) =>
            {
                if (step == "wait" && harness.Admission.Waits.Count == capacity)
                {
                    waiting.TrySetResult();
                }

                return Task.CompletedTask;
            };

            await harness.Clock.TickAsync();

            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Task[] retained = harness.ActiveTasks;

            Assert.Equal(capacity, retained.Length);

            Assert.All(retained, static task => Assert.False(task.IsCompleted));

            IGrimoireExclusiveClosedLease closed = await CloseAsync(harness, closing);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.KeepClosed, CancellationToken.None)).IsSuccess);

            for (int tick = 0; tick < 10; tick++)
            {
                harness.Dispatch();
            }

            Assert.Equal(capacity, harness.Admission.Waits.Count);

            Assert.Equal(capacity, harness.RunningKeys.Length);

            Assert.Equal(startupScopes, harness.Scopes.Created);

            Assert.Equal(0, harness.Gate.EffectGroupAttempts);

            Assert.All(retained, task => Assert.Contains(task, harness.ActiveTasks));

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.All(retained, static task => Assert.True(task.IsCompletedSuccessfully));

            Assert.Empty(harness.ActiveTasks);

            Assert.Empty(harness.RunningKeys);

            Assert.DoesNotContain(harness.Events, static step => step.StartsWith("runner:", StringComparison.Ordinal));
        }
        finally
        {
            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ImmediateRecloseKeepsTheSameTaskAndDoesNotEnterTheRunner()
    {
        await using UnseenServantAdmissionHarness harness = new();

        await harness.ConfigureDueJobAsync();

        IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

        UnseenServantAdmissionHarness.Checkpoint reopened = new();

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        TaskCompletionSource firstWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource secondWait = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.OnStep = (step, token) =>
        {
            if (step == "wait")
            {
                (harness.Admission.Waits.Count == 1 ? firstWait : secondWait).TrySetResult();
            }

            return step == "reopened" ? reopened.PauseAsync(token)
                : step.StartsWith("runner:", StringComparison.Ordinal) ? runner.PauseAsync(token) : Task.CompletedTask;
        };

        using CancellationTokenSource host = new();

        harness.Dispatch(host.Token);

        Task owned = Assert.Single(harness.ActiveTasks);

        try
        {
            await firstWait.Task.WaitAsync(TimeSpan.FromSeconds(10));

            IGrimoireExclusiveClosedLease closed = await CloseAsync(harness, closing);

            Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

            await reopened.WaitAsync();

            IGrimoireClosingOwner secondClosing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            reopened.Release.TrySetResult();

            await secondWait.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Same(owned, Assert.Single(harness.ActiveTasks));

            Assert.DoesNotContain(harness.Events, static step => step.StartsWith("runner:", StringComparison.Ordinal));

            IGrimoireExclusiveClosedLease secondClosed = await CloseAsync(harness, secondClosing);

            Assert.True((await secondClosed.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);

            await runner.WaitAsync();

            runner.Release.TrySetResult();

            await owned.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Single(harness.Events, static step => step.StartsWith("runner:", StringComparison.Ordinal));

            Assert.Equal(2, harness.Admission.Waits.Count);
        }
        finally
        {
            host.Cancel();

            reopened.Release.TrySetResult();

            runner.Release.TrySetResult();

            await owned.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task StopJoinsTheWinningJobsAsyncScopeDisposal()
    {
        await using UnseenServantAdmissionHarness harness = new();

        await harness.ConfigureDueJobAsync();

        await harness.Service.StartAsync(CancellationToken.None);

        UnseenServantAdmissionHarness.Checkpoint disposal = new();

        try
        {
            await harness.Clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));

            harness.OnStep = (step, _) => step == "scope-dispose" ? disposal.PauseAsync() : Task.CompletedTask;

            await harness.Clock.TickAsync();

            await disposal.WaitAsync();

            Task owned = Assert.Single(harness.ActiveTasks);

            Task stop = harness.Service.StopAsync(CancellationToken.None);

            Assert.False(stop.IsCompleted);

            Assert.False(owned.IsCompleted);

            disposal.Release.TrySetResult();

            await stop.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(owned.IsCompletedSuccessfully);

            Assert.Empty(harness.RunningKeys);
        }
        finally
        {
            disposal.Release.TrySetResult();

            await harness.Service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task<IGrimoireExclusiveClosedLease> CloseAsync(UnseenServantAdmissionHarness harness, IGrimoireClosingOwner closing)
    {
        Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        return (await harness.Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));
}
