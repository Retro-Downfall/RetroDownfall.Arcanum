namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Host-owned handle for one admitted human-prompt waiter slot. Dispose releases capacity exactly once;
/// cancellation and <see cref="IHumanPromptRegistry.TrySubmitResponse"/> must not.
/// </summary>
public interface IHumanPromptReservation : IAsyncDisposable
{
    /// <summary>Host-generated correlation id for this reservation.</summary>
    string PromptId { get; }

    /// <summary>
    /// Waits for <see cref="IHumanPromptRegistry.TrySubmitResponse"/> or caller cancellation.
    /// Does not release capacity; the owner must <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    Task<string> WaitAsync(CancellationToken cancellationToken);
}
