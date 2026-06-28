using System.Collections.Concurrent;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// In-process registry: MCP <c>ask_human</c> awaits <see cref="TrySubmitResponse"/> (HTTP or CLI) without blocking threads.
/// </summary>
public sealed class HumanPromptRegistry : IHumanPromptRegistry
{
    // Self-evicts on completion: TrySubmitResponse, the ct.Register callback, and the finally block
    // below all TryRemove the entry (reference-equality guarded). The residual unbounded-growth vector
    // is abandoned ask_human waits with no cancellation (bridged MCP passes Timeout.InfiniteTimeSpan),
    // so the live set is bounded by the inference timeout except for that path. Migrating to a bounded
    // LRU store would be WRONG-SHAPED here: silently dropping a pending TaskCompletionSource leaves the
    // caller awaiting forever, and cancelling an evicted waiter loses the human's eventual response.
    // The audit's recommended fix (waiter cap with explicit error responses + an ask_human-specific
    // timeout) is a separate, larger change tracked as a follow-up to W3.1.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters =
        new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<string> WaitForResponseAsync(string promptId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_waiters.TryAdd(promptId, tcs))
        {
            throw new InvalidOperationException(
                $"A human prompt is already registered for promptId '{promptId}'. Use a new UUID for each ask_human call.");
        }

        try
        {
            using (ct.Register(
                       () =>
                       {
                           if (_waiters.TryRemove(promptId, out TaskCompletionSource<string>? removed)
                               && ReferenceEquals(removed, tcs))
                           {
                               _ = removed.TrySetCanceled(ct);
                           }
                       }))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            // Three-way race: ct.Register callback, TrySubmitResponse, and this finally block
            // all contend on TryRemove. Only one wins the remove; the others see false and no-op.
            // Guard against TaskCanceledException / ObjectDisposedException that can surface if
            // the TCS was already transitioned by a concurrent cancel or submit.
            try
            {
                if (_waiters.TryRemove(promptId, out TaskCompletionSource<string>? left)
                    && ReferenceEquals(left, tcs)
                    && !left.Task.IsCompleted)
                {
                    _ = left.TrySetCanceled(CancellationToken.None);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
            }
        }
    }

    /// <inheritdoc />
    public bool TrySubmitResponse(string promptId, string response)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);

        if (!_waiters.TryRemove(promptId, out TaskCompletionSource<string>? tcs))
        {
            return false;
        }

        return tcs.TrySetResult(response);
    }
}
