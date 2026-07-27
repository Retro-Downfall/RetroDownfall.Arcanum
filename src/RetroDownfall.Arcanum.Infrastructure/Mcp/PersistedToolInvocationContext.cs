namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Identifies a tool call that belongs to an already-persisted assistant turn. Legacy mutating
/// filesystem tools require this context so direct/stateless MCP calls cannot edit outside the
/// normal interaction-persistence pipeline.
/// </summary>
internal sealed record PersistedToolInvocationContext(
    Guid SessionId,
    Guid AssistantEntryId)
{
    internal bool IsValid =>
        SessionId != Guid.Empty
        && AssistantEntryId != Guid.Empty;
}

internal static class PersistedToolInvocationAmbient
{
    private static readonly AsyncLocal<PersistedToolInvocationContext?>
        CurrentValue = new();

    internal static PersistedToolInvocationContext? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    internal static IDisposable Begin(
        PersistedToolInvocationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        PersistedToolInvocationContext? previous = Current;
        Current = context;
        return new Scope(previous);
    }

    private sealed class Scope(
        PersistedToolInvocationContext? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current = previous;
        }
    }
}

internal static class PersistedToolInvocationBinding
{
    private static readonly TimeSpan BindingTtl =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SweepInterval =
        TimeSpan.FromMinutes(1);

    private static readonly
        ExpiringRequestBindingStore<PersistedToolInvocationContext>
        ByRequest = new(
            BindingTtl.Ticks,
            SweepInterval.Ticks,
            static () => DateTime.UtcNow.Ticks);

    internal static void BindRequest(
        string connectionKey,
        string requestId,
        PersistedToolInvocationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(context);

        ByRequest.Bind(connectionKey, requestId, context);
    }

    internal static bool TryResolveRequest(
        string connectionKey,
        string requestId,
        out PersistedToolInvocationContext? context)
    {
        context = null;

        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId)
            || !ByRequest.TryResolve(
                connectionKey,
                requestId,
                out PersistedToolInvocationContext resolved))
        {
            return false;
        }

        context = resolved;
        return true;
    }

    internal static void UnbindRequest(
        string connectionKey,
        string requestId)
    {
        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        ByRequest.Unbind(connectionKey, requestId);
    }
}
