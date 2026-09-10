using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Daemons;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class UnseenServantRetainedJobTests
{
    [Fact]
    public async Task StopClosesDispatchBeforeItsFinalDrainSnapshot()
    {
        await using UnseenServantAdmissionHarness harness = new();

        await harness.ConfigureDueJobAsync();

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        harness.OnStep = (step, _) => step.StartsWith("runner:", StringComparison.Ordinal) ? runner.PauseAsync() : Task.CompletedTask;

        await harness.Service.StopAsync(CancellationToken.None);

        harness.Dispatch();

        Task[] started = harness.ActiveTasks;

        try
        {
            Assert.Empty(started);

            Assert.Empty(harness.RunningKeys);
        }
        finally
        {
            runner.Release.TrySetResult();

            await Task.WhenAll(started).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedJobRetainsFrozenIdentityAndTaskWithoutPublication(bool loseFrontier)
    {
        await using UnseenServantAdmissionHarness harness = new();

        UnseenServantJob job = await harness.ConfigureDueJobAsync();

        harness.Settings.Daemon.MaxConcurrentJobs = 1;

        DateTimeOffset? originalLastRun = harness.Tracker.GetLastRunAt(job);

        IGrimoireClosingOwner? closing = null;

        if (loseFrontier)
        {
            harness.Admission.OnAcquired = _ => closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;
        }
        else
        {
            closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;
        }

        TaskCompletionSource<string> firstWork = new(TaskCreationOptions.RunContinuationsAsynchronously);

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        harness.OnStep = (step, token) =>
        {
            if (step == "wait" || step.StartsWith("runner:", StringComparison.Ordinal))
            {
                firstWork.TrySetResult(step);
            }

            return step.StartsWith("runner:", StringComparison.Ordinal) ? runner.PauseAsync(token) : Task.CompletedTask;
        };

        using CancellationTokenSource host = new();

        harness.Dispatch(host.Token);

        Task owned = Assert.Single(harness.ActiveTasks);

        try
        {
            Assert.Equal("wait", await firstWork.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.False(owned.IsCompleted);

            Assert.Equal(["watch\0patrol"], harness.RunningKeys);

            Assert.Equal(originalLastRun, harness.Tracker.GetLastRunAt(job));

            Assert.Empty(harness.Store.Rows);

            Assert.Equal(0, harness.Scopes.Created);

            Assert.Equal(loseFrontier ? 1 : 0, harness.Gate.EffectGroupAttempts);

            Assert.Single(harness.Admission.Waits);

            if (loseFrontier)
            {
                Assert.Equal(["lease-dispose", "wait"], harness.Events);
            }

            job.Name = "changed";

            job.TargetSpell = "different";

            harness.Dispatch(host.Token);

            Assert.Same(owned, Assert.Single(harness.ActiveTasks));

            harness.Admission.OnAcquired = null;

            await ReopenAsync(harness, closing!);

            await runner.WaitAsync();

            Assert.Contains("runner:unseen-servant:watch", harness.Events);

            runner.Release.TrySetResult();

            await owned.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("watch\0patrol", Assert.Single(harness.Store.Rows).JobKey);

            Assert.Empty(harness.RunningKeys);

            Assert.Empty(harness.ActiveTasks);
        }
        finally
        {
            host.Cancel();

            runner.Release.TrySetResult();

            await owned.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData("runner:unseen-servant:watch")]
    [InlineData("watermark")]
    [InlineData("scope-dispose")]
    [InlineData("group-dispose")]
    [InlineData("lease-dispose")]
    public async Task WinningJobDrainsThroughEveryOwnedBoundary(string boundary)
    {
        await using UnseenServantAdmissionHarness harness = new();

        UnseenServantJob job = await harness.ConfigureDueJobAsync();

        UnseenServantAdmissionHarness.Checkpoint checkpoint = new();

        UnseenServantAdmissionHarness.Checkpoint started = new();

        TaskCompletionSource<string> firstWork = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.OnStep = async (step, _) =>
        {
            firstWork.TrySetResult(step);

            if (step.StartsWith("runner:", StringComparison.Ordinal))
            {
                await started.PauseAsync();
            }

            if (step == boundary)
            {
                await checkpoint.PauseAsync();
            }
        };

        harness.Dispatch();

        Task owned = Assert.Single(harness.ActiveTasks);

        try
        {
            await firstWork.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(1, harness.Gate.EffectGroupAttempts);

            started.Release.TrySetResult();

            await checkpoint.WaitAsync();

            IGrimoireClosingOwner closing = harness.Inner.BeginOrResumeExclusive(Owner()).Value;

            Task<Result> drain = harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None).AsTask();

            Assert.False(drain.IsCompleted);

            if (boundary == "watermark")
            {
                Assert.Equal("Success", harness.Tracker.GetLastResult(job));
            }

            checkpoint.Release.TrySetResult();

            await owned.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);

            Assert.Equal(["scope-dispose", "group-dispose", "lease-dispose"], harness.Events.Where(static step => step.EndsWith("-dispose", StringComparison.Ordinal)));

            Assert.Single(harness.Store.Rows);

            Assert.Empty(harness.Admission.Waits);
        }
        finally
        {
            started.Release.TrySetResult();

            checkpoint.Release.TrySetResult();

            await owned.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(DaemonJobStatus.Failed)]
    [InlineData(DaemonJobStatus.Cancelled)]
    public async Task SuccessfulResultDoesNotTurnFailedOrCancelledSummaryIntoSuccess(DaemonJobStatus status)
    {
        await using UnseenServantAdmissionHarness harness = new();

        UnseenServantJob job = await harness.ConfigureDueJobAsync();

        harness.RunnerStatus = status;

        UnseenServantAdmissionHarness.Checkpoint runner = new();

        harness.OnStep = (step, _) => step.StartsWith("runner:", StringComparison.Ordinal) ? runner.PauseAsync() : Task.CompletedTask;

        harness.Dispatch();

        Task owned = Assert.Single(harness.ActiveTasks);

        await runner.WaitAsync();

        runner.Release.TrySetResult();

        await owned.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(status.ToString(), harness.Tracker.GetLastResult(job));

        Assert.DoesNotContain("Success", harness.Tracker.GetLastResult(job));
    }

    private static async Task ReopenAsync(UnseenServantAdmissionHarness harness, IGrimoireClosingOwner closing)
    {
        Assert.True((await harness.Inner.DrainRequestAndWorkAsync(closing, CancellationToken.None)).IsSuccess);

        IGrimoireExclusiveClosedLease closed = (await harness.Inner.CloseConnectionAdmissionAsync(closing, CancellationToken.None)).Value;

        Assert.True((await closed.CompleteAsync(CovenantExclusiveLeaseDisposition.CommitAndReopen, CancellationToken.None)).IsSuccess);
    }

    private static CovenantExclusiveRecoveryOwner Owner() =>
        new(Guid.NewGuid(), CovenantExclusiveOperation.CovenantReset, new CovenantDigest(new byte[32]));
}
