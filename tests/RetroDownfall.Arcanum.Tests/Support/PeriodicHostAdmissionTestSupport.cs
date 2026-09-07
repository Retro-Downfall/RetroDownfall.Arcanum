using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Tests.Support;

internal sealed class CountingServiceScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
{
    private int _created;

    internal int Created => Volatile.Read(ref _created);

    public IServiceScope CreateScope()
    {
        _ = Interlocked.Increment(ref _created);

        return inner.CreateScope();
    }
}

internal sealed class BlockingAsyncServiceScopeFactory(IServiceProvider services) : IServiceScopeFactory
{
    private int _created;

    internal int Created => Volatile.Read(ref _created);

    internal TaskCompletionSource DisposalReached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource AllowDisposal { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IServiceScope CreateScope()
    {
        _ = Interlocked.Increment(ref _created);

        return new BlockingScope(this, services);
    }

    private sealed class BlockingScope(
        BlockingAsyncServiceScopeFactory owner,
        IServiceProvider services) : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider { get; } = services;

        public void Dispose() =>
            throw new InvalidOperationException("Periodic host scopes must be disposed asynchronously.");

        public async ValueTask DisposeAsync()
        {
            owner.DisposalReached.TrySetResult();

            await owner.AllowDisposal.Task;
        }
    }
}

internal static class PeriodicHostMaintenance
{
    internal static CovenantExclusiveRecoveryOwner Owner() =>
        new(
            Guid.NewGuid(),
            CovenantExclusiveOperation.CovenantReset,
            new CovenantDigest(new byte[32]));

    internal static async Task<IGrimoireExclusiveClosedLease> CloseAsync(
        GrimoireConnectionAdmissionGate gate)
    {
        Result<IGrimoireClosingOwner> beginning = gate.BeginOrResumeExclusive(Owner());

        Assert.True(beginning.IsSuccess, beginning.Error.Message);

        Result drained = await gate.DrainRequestAndWorkAsync(
            beginning.Value,
            CancellationToken.None);

        Assert.True(drained.IsSuccess, drained.Error.Message);

        Result<IGrimoireExclusiveClosedLease> closed = await gate.CloseConnectionAdmissionAsync(
            beginning.Value,
            CancellationToken.None);

        Assert.True(closed.IsSuccess, closed.Error.Message);

        return closed.Value;
    }

    internal static async Task ReopenAsync(IGrimoireExclusiveClosedLease closed)
    {
        Result reopened = await closed.CompleteAsync(
            CovenantExclusiveLeaseDisposition.RollbackAndReopen,
            CancellationToken.None);

        Assert.True(reopened.IsSuccess, reopened.Error.Message);

        await closed.DisposeAsync();
    }
}
