using System.Collections.Concurrent;

namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Correlates in-process JSON-RPC request ids with the caller's <see cref="CancellationToken"/> so
/// <see cref="ArcanumInternalToolServer"/> can honor cooperative cancellation across the MCP boundary.
/// </summary>
internal sealed class McpRequestCancellationBroker
{

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _byRequestId =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Registers <paramref name="callerToken"/> for <paramref name="requestId"/>; must be called before the server handles the request line.
    /// </summary>
    public void Register(string requestId, CancellationToken callerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken);

        if (!_byRequestId.TryAdd(requestId, linked))
        {
            linked.Dispose();

            throw new InvalidOperationException("MCP request broker entry already exists for this id.");
        }
    }

    /// <summary>
    /// Returns the per-request token when registered; otherwise <paramref name="fallback"/>.
    /// </summary>
    public CancellationToken GetTokenOrFallback(string requestId, CancellationToken fallback)
    {
        if (_byRequestId.TryGetValue(requestId, out CancellationTokenSource? cts))
        {
            return cts.Token;
        }

        return fallback;
    }

    /// <summary>
    /// Removes and disposes the registration for <paramref name="requestId"/>.
    /// </summary>
    public void Unregister(string requestId)
    {
        if (_byRequestId.TryRemove(requestId, out CancellationTokenSource? cts))
        {
            cts.Dispose();
        }
    }
}
