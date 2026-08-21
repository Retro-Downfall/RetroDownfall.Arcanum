using RetroDownfall.Arcanum.Infrastructure.Logging;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class DataRetentionServiceTests
{

    internal sealed class CoordinatedManagedLogMutationGate :
        IManagedLogMutationGate,
        IDisposable
    {

        private readonly SemaphoreSlim _gate = new(1, 1);

        private readonly TaskCompletionSource _firstReleaseRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _allowFirstRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _secondAttempted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _attempts;

        public Task FirstReleaseRequested =>
            _firstReleaseRequested.Task;

        public Task SecondAttempted =>
            _secondAttempted.Task;

        public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
            CancellationToken cancellationToken = default)
        {

            int attempt = Interlocked.Increment(ref _attempts);

            if (attempt == 2)
            {

                _secondAttempted.TrySetResult();

            }

            await _gate.WaitAsync(cancellationToken);

            return new Lease(
                this,
                attempt);

        }

        public void AllowFirstRelease() =>
            _allowFirstRelease.TrySetResult();

        public void Dispose() =>
            _gate.Dispose();

        private async ValueTask ReleaseAsync(int attempt)
        {

            if (attempt == 1)
            {

                _firstReleaseRequested.TrySetResult();

                await _allowFirstRelease.Task;

            }

            _gate.Release();

        }

        private sealed class Lease(
            CoordinatedManagedLogMutationGate owner,
            int attempt) : IAsyncDisposable
        {

            private int _disposed;

            public async ValueTask DisposeAsync()
            {

                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {

                    return;

                }

                await owner.ReleaseAsync(attempt);

            }

        }

    }

}
