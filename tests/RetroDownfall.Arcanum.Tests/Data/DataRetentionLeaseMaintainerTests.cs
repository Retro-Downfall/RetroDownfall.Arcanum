using System.Runtime.CompilerServices;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed class DataRetentionLeaseMaintainerTests
{
    [Fact]

    public async Task RunAsync_RenewsLeaseUntilSlowCandidateCompletes()
    {
        int renewals = 0;

        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DataRetentionLeaseMaintainer maintainer = new(
            (_, _, _, _, _) =>
            {
                if (Interlocked.Increment(ref renewals) == 2)
                {
                    completion.TrySetResult();
                }

                return Task.FromResult(true);
            },
            TimeProvider.System,
            leaseDuration: TimeSpan.FromSeconds(1),
            heartbeatInterval: TimeSpan.FromMilliseconds(20));

        int result = await maintainer.RunAsync(
            Guid.NewGuid(),
            "retention-owner",
            async cancellationToken =>
            {
                await completion.Task.WaitAsync(cancellationToken);

                return 42;
            },
            CancellationToken.None);

        Assert.Equal(42, result);

        Assert.True(renewals >= 2);
    }

    [Fact]

    public async Task RunAsync_WhenRenewalFails_CancelsCandidateAndFailsClosed()
    {
        TaskCompletionSource cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DataRetentionLeaseMaintainer maintainer = new(
            (_, _, _, _, _) => Task.FromResult(false),
            TimeProvider.System,
            leaseDuration: TimeSpan.FromSeconds(1),
            heartbeatInterval: TimeSpan.FromMilliseconds(20));

        DataRetentionLeaseLostException error =
            await Assert.ThrowsAsync<DataRetentionLeaseLostException>(
                () => maintainer.RunAsync(
                    Guid.NewGuid(),
                    "retention-owner",
                    async cancellationToken =>
                    {
                        try
                        {
                            await Task.Delay(
                                Timeout.InfiniteTimeSpan,
                                cancellationToken);

                            return 42;
                        }
                        finally
                        {
                            cancellationObserved.TrySetResult();
                        }
                    },
                    CancellationToken.None));

        Assert.Equal(
            "The retention operation lost its durable lease while applying a candidate.",
            error.Message);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RunAsync_WhenCancellationCallbackThrows_JoinsActionBeforeAggregatingFailures()
    {
        const string callbackSecret = "callback-secret-should-not-escape";

        DataRetentionLeaseLostException primaryFailure = new(
            "The retention operation lost its synthetic lease.");

        TaskCompletionSource callbackEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource releaseAction = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DataRetentionLeaseMaintainer maintainer = new(
            (_, _, _, _, _) => Task.FromException<bool>(primaryFailure),
            TimeProvider.System,
            leaseDuration: TimeSpan.FromSeconds(1),
            heartbeatInterval: TimeSpan.FromMilliseconds(20));

        Task<int> running = maintainer.RunAsync(
            Guid.NewGuid(),
            "retention-owner",
            async cancellationToken =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    callbackEntered.TrySetResult();

                    throw new InvalidOperationException(callbackSecret);
                });

                await releaseAction.Task;

                return 42;
            },
            CancellationToken.None);

        bool completedBeforeActionRelease = false;

        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Task earlyWinner = await Task.WhenAny(
                running,
                Task.Delay(TimeSpan.FromMilliseconds(250)));

            completedBeforeActionRelease = ReferenceEquals(earlyWinner, running);
        }
        finally
        {
            releaseAction.TrySetResult();
        }

        Exception? observed = await Record.ExceptionAsync(
            async () => _ = await running.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.False(completedBeforeActionRelease);

        AggregateException error = Assert.IsType<AggregateException>(observed);

        Assert.Collection(
            error.InnerExceptions,
            primary => Assert.Same(primaryFailure, primary),
            cancellation =>
            {
                DataRetentionCancellationCallbackException sanitized =
                    Assert.IsType<DataRetentionCancellationCallbackException>(cancellation);

                Assert.Equal(
                    "One or more retention action cancellation callbacks failed.",
                    sanitized.Message);

                Assert.Null(sanitized.InnerException);
            });

        Assert.DoesNotContain(callbackSecret, error.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(callbackSecret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]

    public async Task RunAsync_WhenTerminalActionCompletesDuringRenewal_ReturnsTheTerminalResult()
    {
        TaskCompletionSource renewalStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource allowRenewal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<int> terminalResult = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        DataRetentionLeaseMaintainer maintainer = new(
            async (_, _, _, _, cancellationToken) =>
            {
                renewalStarted.TrySetResult();

                await allowRenewal.Task.WaitAsync(cancellationToken);

                return false;
            },
            TimeProvider.System,
            leaseDuration: TimeSpan.FromSeconds(1),
            heartbeatInterval: TimeSpan.FromMilliseconds(20));

        Task<int> running = maintainer.RunAsync(
            Guid.NewGuid(),
            "retention-terminal-owner",
            _ => terminalResult.Task,
            CancellationToken.None);

        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        terminalResult.TrySetResult(42);

        allowRenewal.TrySetResult();

        Assert.Equal(42, await running.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]

    public async Task RunAsync_WhenActionWins_CancelsOutstandingHeartbeatTimerBeforeReturning()
    {
        TrackingTimeProvider timeProvider = new();

        TaskCompletionSource<int> actionCompletion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        int renewals = 0;

        DataRetentionLeaseMaintainer maintainer = new(
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref renewals);

                return Task.FromResult(true);
            },
            timeProvider,
            leaseDuration: TimeSpan.FromMinutes(2),
            heartbeatInterval: TimeSpan.FromMinutes(1));

        Task<int> running = maintainer.RunAsync(
            Guid.NewGuid(),
            "retention-owner",
            _ => actionCompletion.Task,
            CancellationToken.None);

        await timeProvider.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(actionCompletion.TrySetResult(42));

        Assert.Equal(42, await running.WaitAsync(TimeSpan.FromSeconds(1)));

        await timeProvider.TimerDisposed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, timeProvider.Disposals);

        Assert.Equal(0, Volatile.Read(ref renewals));
    }

    [Fact]

    public void RunAsync_FinallyObservesTheCancelledLosingHeartbeatDelay()
    {
        string source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Data",
                "DataRetentionLeaseMaintainer.cs"));

        string compactSource = string.Concat(
            source.Where(static character => !char.IsWhiteSpace(character)));

        Assert.Contains(
            "finally{try{heartbeatCancellation.Cancel();}finally{"
                + "if(pendingHeartbeatDelayisnotnull){"
                + "awaitObserveCompletionAsync(pendingHeartbeatDelay).ConfigureAwait(false);",
            compactSource,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "") =>
        Path.GetFullPath(
            Path.Combine(
                Path.GetDirectoryName(sourceFilePath)!,
                "..",
                "..",
                ".."));

    private sealed class TrackingTimeProvider : TimeProvider
    {
        private int _disposals;

        internal TaskCompletionSource TimerCreated { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource TimerDisposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal int Disposals => Volatile.Read(ref _disposals);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            TimerCreated.TrySetResult();

            return new TrackingTimer(this);
        }

        private void RecordDisposal()
        {
            _ = Interlocked.Increment(ref _disposals);

            TimerDisposed.TrySetResult();
        }

        private sealed class TrackingTimer(TrackingTimeProvider owner) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.RecordDisposal();
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();

                return ValueTask.CompletedTask;
            }
        }
    }
}
