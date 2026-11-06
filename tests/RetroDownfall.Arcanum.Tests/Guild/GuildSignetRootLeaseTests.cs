using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Guild;

namespace RetroDownfall.Arcanum.Tests.Guild;

public sealed class GuildSignetRootLeaseTests
{
    [Fact]
    public async Task Imperative_root_scope_refuses_nested_roots_and_reclaims_after_idempotent_disposal()
    {
        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        IDisposable lease = await AcquireAsync(gate, Guid.NewGuid(), default);

        Assert.True(Enter(lease));

        Assert.True((await GuildSignetRootGate.RunAsync<int>(gate, Guid.NewGuid(), _ => Task.FromResult(Result<int>.Success(1)), default)).IsFailure);

        lease.Dispose();

        lease.Dispose();

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(gate));

        Assert.True((await GuildSignetRootGate.RunAsync<int>(gate, Guid.NewGuid(), _ => Task.FromResult(Result<int>.Success(1)), default)).IsSuccess);
    }

    [Fact]
    public async Task Throwing_handoff_releases_the_imperative_root_scope()
    {
        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using IDisposable lease = await AcquireAsync(gate, Guid.NewGuid(), default);

            Assert.True(Enter(lease));

            throw new InvalidOperationException("handoff failed");
        });

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(gate));
    }

    [Fact]
    public async Task A_canceled_imperative_waiter_does_not_leak_the_root_entry()
    {
        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        Guid root = Guid.NewGuid();

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result<int>> holder = GuildSignetRootGate.RunAsync<int>(gate, root, async _ =>
        {
            entered.SetResult();

            await release.Task;

            return 1;
        }, default);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using CancellationTokenSource cancellation = new();

        Task<GuildSignetRootGate.RootLease> waiter = AcquireAsync(gate, root, cancellation.Token);

        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True((await holder).IsSuccess);

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(gate));
    }

    [Fact]
    public async Task Caller_context_clears_its_marker_after_child_context_released_the_resource()
    {
        GrimoireConnectionAdmissionGate gate = new(TimeProvider.System);

        var first = await AcquireAsync(gate, Guid.NewGuid(), default);

        Assert.True(first.Enter());

        await DisposeInChildContextAsync(first);

        first.Dispose();

        var next = await GuildSignetRootGate.AcquireAsync(gate, Guid.NewGuid(), default);

        Assert.True(next.IsSuccess, next.Error.Message);

        using (var held = next.Value)
        {
            Assert.True(held.Enter());

            first.Dispose();

            Assert.True((await GuildSignetRootGate.RunAsync<int>(gate, Guid.NewGuid(),
                _ => Task.FromResult(Result<int>.Success(1)), default)).IsFailure);
        }

        Assert.Equal(0, GuildSignetRootGate.RetainedRootCount(gate));
    }

    private static async Task DisposeInChildContextAsync(IDisposable lease)
    {
        await Task.Yield();

        lease.Dispose();
    }

    private static async Task<GuildSignetRootGate.RootLease> AcquireAsync(IGrimoireConnectionAdmissionGate gate, Guid root, CancellationToken ct)
    {
        var acquired = await GuildSignetRootGate.AcquireAsync(gate, root, ct);

        Assert.True(acquired.IsSuccess, acquired.Error.Message);

        return acquired.Value;
    }

    private static bool Enter(IDisposable lease) => Assert.IsType<GuildSignetRootGate.RootLease>(lease).Enter();
}
