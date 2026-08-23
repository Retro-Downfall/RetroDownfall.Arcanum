namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// The one process-wide gate every host-tools marker mutation is held under.
/// </summary>
/// <remarks>
/// Two things mutate this slot: the taint transition that writes the marker, and the full
/// installation reset that deletes it. They never run for the same reason and would never normally
/// meet, which is exactly why the exclusion has to be structural rather than incidental — a delete
/// interleaved with a write leaves a slot whose contents belong to neither operation, and both of
/// them would then read back something they can neither adopt nor refuse cleanly.
///
/// <para>One instance, shared. A gate constructed per service is two gates, and two gates exclude
/// nothing from each other.</para>
/// </remarks>
internal sealed class HostProcessToolsMarkerMutationGate
{

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Waits for exclusive ownership and hands back the lease that releases it.
    /// </summary>
    /// <remarks>
    /// Cancellation is honoured before ownership and never after: a caller cancelled while waiting
    /// never held the gate, and a caller that already holds it has to release it however its own
    /// operation ends. The lease releases exactly once no matter how often it is disposed, because
    /// a double release would raise the count above one and let a second writer in behind the first.
    /// </remarks>
    internal async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(
        CancellationToken cancellationToken = default)
    {

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new Lease(_gate);

    }

    private sealed class Lease(SemaphoreSlim gate) : IAsyncDisposable
    {

        private int _released;

        public ValueTask DisposeAsync()
        {

            if (Interlocked.Exchange(ref _released, 1) == 0)
            {

                _ = gate.Release();

            }

            return ValueTask.CompletedTask;

        }

    }

}
