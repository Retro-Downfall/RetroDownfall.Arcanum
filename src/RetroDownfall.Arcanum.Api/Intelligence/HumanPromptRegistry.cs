using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// In-process registry: MCP <c>ask_human</c> and standard MCP elicitation await
/// <see cref="TrySubmitResponse"/> (HTTP or CLI) without blocking threads.
/// Capacity is owned by <see cref="IHumanPromptReservation"/>; submit/cancel complete
/// the waiter but do not release the admission slot.
/// </summary>
public sealed class HumanPromptRegistry : IHumanPromptRegistry
{

    /// <summary>
    /// Active waiter capacity. Additional reservations wait cancellably for a slot.
    /// </summary>
    public const int MaxConcurrentWaiters = 64;

    private readonly SemaphoreSlim _admission = new(MaxConcurrentWaiters, MaxConcurrentWaiters);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IHumanPromptReservation> CreateReservationAsync(
        CancellationToken cancellationToken)
    {
        return CreateReservationCoreAsync(
            Guid.NewGuid().ToString("N"),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> AwaitReservedAsync(
        string promptId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        if (!_waiters.TryGetValue(promptId, out TaskCompletionSource<string>? tcs))
        {
            throw new InvalidOperationException(
                $"No human prompt reservation exists for promptId '{promptId}'.");
        }

        return AwaitExistingAsync(promptId, tcs, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> WaitForResponseAsync(string promptId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        IHumanPromptReservation reservation = await CreateReservationCoreAsync(
                promptId,
                cancellationToken)
            .ConfigureAwait(false);

        await using (reservation.ConfigureAwait(false))
        {
            return await reservation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public bool TrySubmitResponse(string promptId, string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        if (!_waiters.TryGetValue(promptId, out TaskCompletionSource<string>? tcs))
        {
            return false;
        }

        // Completes the waiter only — capacity stays held until the reservation owner disposes.
        return tcs.TrySetResult(response);
    }

    /// <summary>Current waiter count for assertions in the test suite.</summary>
    internal int WaiterCountForTesting => _waiters.Count;

    /// <summary>Remaining admission slots for assertions in the test suite.</summary>
    internal int AvailableSlotsForTesting => _admission.CurrentCount;

    private async Task<IHumanPromptReservation> CreateReservationCoreAsync(
        string promptId,
        CancellationToken cancellationToken)
    {
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!_waiters.TryAdd(promptId, tcs))
            {
                throw new InvalidOperationException(
                    $"A human prompt is already registered for promptId '{promptId}'. Use a new UUID for each ask_human call.");
            }

            return new Reservation(this, promptId, tcs);
        }
        catch
        {
            _admission.Release();

            throw;
        }
    }

    private async Task<string> AwaitExistingAsync(
        string promptId,
        TaskCompletionSource<string> tcs,
        CancellationToken cancellationToken)
    {
        using (cancellationToken.Register(
                   () =>
                   {
                       if (!_waiters.TryGetValue(promptId, out TaskCompletionSource<string>? current)
                           || !ReferenceEquals(current, tcs))
                       {
                           return;
                       }

                       _ = tcs.TrySetCanceled(cancellationToken);
                   }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private void ReleaseReservation(string promptId, TaskCompletionSource<string> tcs)
    {
        if (_waiters.TryRemove(promptId, out TaskCompletionSource<string>? removed)
            && ReferenceEquals(removed, tcs)
            && !removed.Task.IsCompleted)
        {
            _ = removed.TrySetCanceled(CancellationToken.None);
        }

        _admission.Release();
    }

    private sealed class Reservation : IHumanPromptReservation
    {
        private readonly HumanPromptRegistry _registry;
        private readonly TaskCompletionSource<string> _tcs;
        private int _disposed;

        public Reservation(
            HumanPromptRegistry registry,
            string promptId,
            TaskCompletionSource<string> tcs)
        {
            _registry = registry;
            PromptId = promptId;
            _tcs = tcs;
        }

        public string PromptId { get; }

        public Task<string> WaitAsync(CancellationToken cancellationToken)
        {
            return _registry.AwaitExistingAsync(PromptId, _tcs, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            _registry.ReleaseReservation(PromptId, _tcs);

            return ValueTask.CompletedTask;
        }
    }

}
