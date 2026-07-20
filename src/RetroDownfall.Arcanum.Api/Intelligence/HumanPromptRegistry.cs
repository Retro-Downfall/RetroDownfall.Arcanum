using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// In-process registry: MCP <c>ask_human</c> and standard MCP elicitation await
/// <see cref="TrySubmitResponse"/> (HTTP or CLI) without blocking threads.
/// Capacity is owned by <see cref="IHumanPromptReservation"/>; submit/timeout/cancel complete
/// the waiter but do not release the admission slot.
/// </summary>
public sealed class HumanPromptRegistry : IHumanPromptRegistry
{

    /// <summary>
    /// Soft cap on concurrent waiters. Excess admissions fail rather than growing without bound.
    /// </summary>
    public const int MaxConcurrentWaiters = 64;

    /// <summary>
    /// Hard ceiling leak guard when the caller token never cancels. Prefer linked inference/stream
    /// cancellation first (default inference timeout is typically 10 minutes); this ceiling is only
    /// a backstop.
    /// </summary>
    public static readonly TimeSpan HardCeiling = TimeSpan.FromMinutes(30);

    /// <summary>Overridable in tests to exercise the hard-ceiling path without waiting 30 minutes.</summary>
    internal TimeSpan CeilingForTesting { get; set; } = HardCeiling;

    private readonly SemaphoreSlim _admission = new(MaxConcurrentWaiters, MaxConcurrentWaiters);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IHumanPromptReservation? TryCreateReservation()
    {
        return TryCreateReservationCore(Guid.NewGuid().ToString("N"));
    }

    /// <inheritdoc />
    public Task<string> AwaitReservedAsync(
        string promptId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        if (!_waiters.TryGetValue(promptId, out TaskCompletionSource<string>? tcs))
        {
            throw new InvalidOperationException(
                $"No human prompt reservation exists for promptId '{promptId}'.");
        }

        return AwaitExistingAsync(promptId, tcs, timeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> WaitForResponseAsync(string promptId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        IHumanPromptReservation? reservation = TryCreateReservationCore(promptId);

        if (reservation is null)
        {
            throw new HumanPromptCapExceededException();
        }

        await using (reservation.ConfigureAwait(false))
        {
            return await reservation.WaitAsync(CeilingForTesting, cancellationToken).ConfigureAwait(false);
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

    private IHumanPromptReservation? TryCreateReservationCore(string promptId)
    {
        if (!_admission.Wait(0))
        {
            return null;
        }

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_waiters.TryAdd(promptId, tcs))
        {
            _admission.Release();

            throw new InvalidOperationException(
                $"A human prompt is already registered for promptId '{promptId}'. Use a new UUID for each ask_human call.");
        }

        return new Reservation(this, promptId, tcs);
    }

    private async Task<string> AwaitExistingAsync(
        string promptId,
        TaskCompletionSource<string> tcs,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        TimeSpan effectiveTimeout = NormalizeTimeout(timeout);

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        linkedCts.CancelAfter(effectiveTimeout);

        try
        {
            using (linkedCts.Token.Register(
                       () =>
                       {
                           // Timeout/cancel complete the waiter but must not release capacity or
                           // remove the reservation — the owner dispose does that.
                           if (!_waiters.TryGetValue(promptId, out TaskCompletionSource<string>? current)
                               || !ReferenceEquals(current, tcs))
                           {
                               return;
                           }

                           if (cancellationToken.IsCancellationRequested)
                           {
                               _ = tcs.TrySetCanceled(cancellationToken);
                           }
                           else
                           {
                               _ = tcs.TrySetException(new HumanPromptTimeoutException());
                           }
                       }))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        catch (HumanPromptTimeoutException)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw new HumanPromptTimeoutException();
            }

            throw;
        }
    }

    private TimeSpan NormalizeTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > CeilingForTesting)
        {
            return CeilingForTesting;
        }

        return timeout;
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

        public Task<string> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            return _registry.AwaitExistingAsync(PromptId, _tcs, timeout, cancellationToken);
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
