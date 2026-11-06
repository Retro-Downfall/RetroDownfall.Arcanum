using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSignetRootGateTests
{
    [Fact]
    public async Task Finished_root_admissions_do_not_leave_an_installation_lifetime_cache_of_semaphores()
    {
        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        for (int index = 0; index < 128; index++)
        {
            Assert.True((await GuildSignetRootGate.RunAsync<int>(gate, Guid.NewGuid(),
                _ => Task.FromResult(Result<int>.Success(1)), default)).IsSuccess);
        }

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(gate));
    }

    [Fact]
    public async Task Cancellation_of_a_waiter_cannot_split_the_root_ordering_point_or_retain_it_after_drain()
    {
        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Guid root = Guid.NewGuid();

        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        int active = 0;

        Task<Result<int>> first = GuildSignetRootGate.RunAsync<int>(gate, root, async _ =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));

            firstEntered.SetResult();

            await release.Task;

            Interlocked.Decrement(ref active);

            return 1;
        }, default);

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using CancellationTokenSource cancellation = new();

        Task<Result<int>> canceled = GuildSignetRootGate.RunAsync<int>(gate, root,
            _ => Task.FromResult(Result<int>.Success(2)), cancellation.Token);

        Task<Result<int>> next = GuildSignetRootGate.RunAsync<int>(gate, root, _ =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));

            Interlocked.Decrement(ref active);

            return Task.FromResult(Result<int>.Success(3));
        }, default);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        Assert.False(next.IsCompleted);

        release.SetResult();

        Assert.True((await first).IsSuccess);

        Assert.Equal(3, (await next).Value);

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(gate));
    }
}
