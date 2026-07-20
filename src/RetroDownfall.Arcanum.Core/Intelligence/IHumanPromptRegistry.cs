namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Correlates in-flight human prompts (e.g. MCP <c>ask_human</c>) with HTTP or CLI responses.
/// Preferred lifecycle: <see cref="TryCreateReservation"/> → emit → await → dispose once.
/// </summary>
public interface IHumanPromptRegistry
{
    /// <summary>
    /// Atomically admits one waiter slot and registers a host-generated prompt id.
    /// Returns <see langword="null"/> when the concurrent waiter cap is exhausted.
    /// </summary>
    IHumanPromptReservation? TryCreateReservation();

    /// <summary>
    /// Waits on an already-reserved prompt without re-registering or releasing capacity.
    /// Used by the internal <c>ask_human</c> tool after the host has reserved.
    /// </summary>
    Task<string> AwaitReservedAsync(string promptId, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy combined reserve+wait+dispose for callers that have not migrated to explicit reservation ownership
    /// (e.g. elicitation before the prepared path). Registers <paramref name="promptId"/>, waits, then releases.
    /// </summary>
    Task<string> WaitForResponseAsync(string promptId, CancellationToken cancellationToken);

    /// <summary>
    /// Completes the wait for <paramref name="promptId"/> when one is registered.
    /// Does not release capacity — the reservation owner must dispose.
    /// Returns <see langword="false"/> if no waiter exists or the waiter already completed.
    /// </summary>
    bool TrySubmitResponse(string promptId, string response);
}
