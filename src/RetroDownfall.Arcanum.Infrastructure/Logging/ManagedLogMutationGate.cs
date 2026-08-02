namespace RetroDownfall.Arcanum.Infrastructure.Logging;

internal interface IManagedLogMutationGate
{

    ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        CancellationToken cancellationToken = default);

}

internal sealed class ManagedLogMutationGate :
    IManagedLogMutationGate
{

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        CancellationToken cancellationToken = default)
    {

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Lease(_gate);

    }

    private sealed class Lease(
        SemaphoreSlim gate) : IAsyncDisposable
    {

        private int _disposed;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {

                gate.Release();

            }

            return ValueTask.CompletedTask;

        }

    }

}
