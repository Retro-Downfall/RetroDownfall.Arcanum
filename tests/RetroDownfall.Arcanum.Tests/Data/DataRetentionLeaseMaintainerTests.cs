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

}
