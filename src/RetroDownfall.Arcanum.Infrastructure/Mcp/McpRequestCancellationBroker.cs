using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Correlates in-process JSON-RPC request ids with the caller's <see cref="CancellationToken"/> so
/// <see cref="ArcanumInternalToolServer"/> can honor cooperative cancellation across the MCP boundary.
/// </summary>
/// <remarks>
/// Each <see cref="Register"/> attaches a <see cref="CancellationToken.Register(Action{object?}, object?)"/> on
/// the caller's token so that, even if <see cref="Unregister"/> is not called (caller crash, deadlock,
/// timeout), the entry is removed and the linked <see cref="CancellationTokenSource"/> is disposed
/// when the original token's lifetime ends. This eliminates the per-request leak window otherwise
/// possible across a long-running process.
/// </remarks>
internal sealed class McpRequestCancellationBroker
{

    private readonly ConcurrentDictionary<string, BrokerEntry> _byRequestId =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="callerToken"/> for <paramref name="requestId"/>; must be called before the server handles the request line.
    /// </summary>
    public void Register(string requestId, CancellationToken callerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        BrokerEntry entry = new(linked, callerToken);

        if (!_byRequestId.TryAdd(requestId, entry))
        {
            linked.Dispose();

            throw new InvalidOperationException("MCP request broker entry already exists for this id.");
        }

        if (!callerToken.CanBeCanceled)
        {
            return;
        }

        BrokerCleanupState state = new(this, requestId);

        CancellationTokenRegistration tokenReg = callerToken.Register(
            static s => ((BrokerCleanupState)s!).CleanupAfterCallerToken(),
            state);

        if (!entry.TrySetCancellationRegistration(tokenReg))
        {
            tokenReg.Dispose();
        }
    }

    /// <summary>
    /// Returns the per-request token when registered; otherwise <paramref name="fallback"/>.
    /// </summary>
    public CancellationToken GetTokenOrFallback(string requestId, CancellationToken fallback)
    {
        if (_byRequestId.TryGetValue(requestId, out BrokerEntry? entry))
        {
            return entry.CallerToken;
        }

        return fallback;
    }

    /// <summary>
    /// Removes and disposes the registration for <paramref name="requestId"/>.
    /// </summary>
    public void Unregister(string requestId)
    {
        if (_byRequestId.TryRemove(requestId, out BrokerEntry? entry))
        {
            entry.Dispose();
        }
    }

    private sealed class BrokerEntry(CancellationTokenSource linkedSource, CancellationToken callerToken) : IDisposable
    {

        private readonly object _gate = new();

        private CancellationTokenRegistration _callerRegistration;

        private bool _disposed;

        public CancellationToken CallerToken => callerToken;

        public CancellationTokenSource LinkedSource => linkedSource;

        /// <summary>
        /// Stores the cancellation registration if the entry has not been disposed. Returns
        /// <c>false</c> when the entry was already disposed concurrently, signaling the caller
        /// to dispose the registration itself so no native handle leaks.
        /// </summary>
        public bool TrySetCancellationRegistration(CancellationTokenRegistration registration)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return false;
                }

                _callerRegistration = registration;

                return true;
            }
        }

        public void Dispose()
        {
            CancellationTokenRegistration registrationToDispose;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                registrationToDispose = _callerRegistration;

                _callerRegistration = default;
            }

            registrationToDispose.Dispose();

            linkedSource.Dispose();
        }

    }

    private sealed class BrokerCleanupState(McpRequestCancellationBroker broker, string requestId)
    {

        public void CleanupAfterCallerToken()
        {
            broker.Unregister(requestId);
        }

    }

}
