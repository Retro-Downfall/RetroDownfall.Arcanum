using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace RetroDownfall.Arcanum.Infrastructure.A2A;

/// <summary>
/// Live index of outbound Sendings waiting on a peer callback, and the endpoint's way of waking them.
/// </summary>
/// <remarks>
/// <para>
/// Callback mode's whole point is that the waiting Sending is <em>not</em> holding a concurrency slot, so
/// something has to connect an inbound HTTP POST back to the awaiting dispatch. This is that something —
/// process memory, and deliberately so: the durable half of the correspondence lives in the Sending
/// ledger, and a callback that arrives after a restart settles the ledger row instead of an awaiter that
/// no longer exists (issue #67).
/// </para>
/// <para>
/// The token is authenticated against this index only for live waiters. A callback for a Sending
/// registered by a previous process is authenticated against the digest on its durable record, which is
/// the same check against the same secret.
/// </para>
/// </remarks>
public sealed class A2ASendingCallbackRegistry(ILogger<A2ASendingCallbackRegistry> logger)
{

    private readonly ConcurrentDictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);

    private sealed record Waiter(string TokenHash, SemaphoreSlim Signal);

    /// <summary>
    /// Registers a waiter for <paramref name="configId"/>. Dispose the returned handle when the Sending
    /// ends, however it ends — a leaked waiter is a leaked callback endpoint.
    /// </summary>
    /// <param name="signal">
    /// Released once per delivered callback. A counting signal rather than a one-shot completion because
    /// a peer may post several transitions before the task settles, and each one is a reason to re-read
    /// the task rather than to conclude it is finished.
    /// </param>
    public IDisposable Register(string configId, string tokenHash, SemaphoreSlim signal)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(configId);

        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        ArgumentNullException.ThrowIfNull(signal);

        _waiters[configId] = new Waiter(tokenHash, signal);

        return new Registration(this, configId);

    }

    /// <summary>
    /// Wakes the Sending waiting on <paramref name="configId"/>, if the presented token matches.
    /// </summary>
    /// <returns>
    /// <see cref="A2ACallbackOutcome.Delivered"/> when a live waiter was woken,
    /// <see cref="A2ACallbackOutcome.Unauthenticated"/> when the token did not match, and
    /// <see cref="A2ACallbackOutcome.NoLiveWaiter"/> when nothing in this process is waiting — which is
    /// the after-a-restart case, not an error.
    /// </returns>
    public A2ACallbackOutcome TrySignal(string configId, string? presentedToken)
    {

        if (string.IsNullOrWhiteSpace(configId) || !_waiters.TryGetValue(configId, out Waiter? waiter))
        {

            return A2ACallbackOutcome.NoLiveWaiter;

        }

        if (!A2ACallbackToken.Matches(presentedToken, waiter.TokenHash))
        {

            logger.LogWarning(
                "A2A: rejected a Sending callback for config {ConfigId} because its token did not match.",
                configId);

            return A2ACallbackOutcome.Unauthenticated;

        }

        try
        {

            waiter.Signal.Release();

        }
        catch (ObjectDisposedException)
        {

            // The Sending ended between the lookup and the wake; there is nothing left to tell.
            return A2ACallbackOutcome.NoLiveWaiter;

        }

        return A2ACallbackOutcome.Delivered;

    }

    private void Remove(string configId) => _waiters.TryRemove(configId, out _);

    private sealed class Registration(A2ASendingCallbackRegistry owner, string configId) : IDisposable
    {

        private bool _disposed;

        public void Dispose()
        {

            if (_disposed)
            {

                return;

            }

            _disposed = true;

            owner.Remove(configId);

        }

    }

}

/// <summary>What happened to an inbound Sending callback.</summary>
public enum A2ACallbackOutcome
{

    /// <summary>Nothing in this process is waiting on that config id.</summary>
    NoLiveWaiter = 0,

    /// <summary>A live Sending was woken.</summary>
    Delivered = 1,

    /// <summary>The presented token did not match the registered one.</summary>
    Unauthenticated = 2,

}
