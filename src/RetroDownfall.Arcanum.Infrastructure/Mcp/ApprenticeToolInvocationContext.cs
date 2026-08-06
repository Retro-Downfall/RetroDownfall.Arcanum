namespace RetroDownfall.Arcanum.Infrastructure.Mcp;

/// <summary>
/// Identifies the Apprentice whose execution loop issued an internal MCP tool call, and the A2A
/// delegation chain that Apprentice inherited when it was spawned.
/// </summary>
/// <remarks>
/// The in-process MCP server is <em>workspace</em>-scoped, not Apprentice-scoped: one server instance
/// serves every Apprentice sharing a workspace, so a tool handler cannot tell which Apprentice is
/// calling. Without that, <c>dispatch_sending</c> restarts the delegation chain from empty and a
/// multi-hop cycle (A &#8594; B &#8594; C &#8594; A) is invisible — only the direct A &#8594; B &#8594; A case is caught
/// (issue #59, docs/Arcanum.DESIGN.md &#167;5.7.1).
/// </remarks>
internal sealed record ApprenticeToolInvocationContext(
    Guid ApprenticeId,
    IReadOnlyList<string> DelegationChain)
{

    internal bool IsValid => ApprenticeId != Guid.Empty;

}

/// <summary>
/// Caller-side ambient for <see cref="ApprenticeToolInvocationContext"/>. Set by the Apprentice
/// execution loop around a step's inference so the MCP client send boundary can bind it to the
/// outgoing <c>tools/call</c> request id.
/// </summary>
internal static class ApprenticeToolInvocationAmbient
{

    private static readonly AsyncLocal<ApprenticeToolInvocationContext?> CurrentValue = new();

    internal static ApprenticeToolInvocationContext? Current
    {
        get => CurrentValue.Value;
        set => CurrentValue.Value = value;
    }

    internal static IDisposable Begin(ApprenticeToolInvocationContext context)
    {

        ArgumentNullException.ThrowIfNull(context);

        ApprenticeToolInvocationContext? previous = Current;

        Current = context;

        return new Scope(previous);

    }

    private sealed class Scope(ApprenticeToolInvocationContext? previous) : IDisposable
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

/// <summary>
/// Request-id keyed transfer of <see cref="ApprenticeToolInvocationContext"/> from the MCP client send
/// boundary to the in-process server.
/// </summary>
/// <remarks>
/// The in-process transport runs the server on its own task, so an <see cref="AsyncLocal{T}"/> set by
/// the Apprentice loop does not flow into the handler. This mirrors
/// <see cref="PersistedToolInvocationBinding"/> and <c>ApplyPatchInvocationBinding</c> rather than
/// inventing a third mechanism, and the binding is removed as soon as the call settles.
/// </remarks>
internal static class ApprenticeToolInvocationBinding
{

    private static readonly TimeSpan BindingTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private static readonly ExpiringRequestBindingStore<ApprenticeToolInvocationContext> ByRequest = new(
        BindingTtl.Ticks,
        SweepInterval.Ticks,
        static () => DateTime.UtcNow.Ticks);

    internal static void BindRequest(
        string connectionKey,
        string requestId,
        ApprenticeToolInvocationContext context)
    {

        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);

        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        ArgumentNullException.ThrowIfNull(context);

        ByRequest.Bind(connectionKey, requestId, context);

    }

    internal static bool TryResolveRequest(
        string connectionKey,
        string requestId,
        out ApprenticeToolInvocationContext? context)
    {

        context = null;

        if (string.IsNullOrWhiteSpace(connectionKey)
            || string.IsNullOrWhiteSpace(requestId)
            || !ByRequest.TryResolve(connectionKey, requestId, out ApprenticeToolInvocationContext resolved))
        {

            return false;

        }

        context = resolved;

        return true;

    }

    internal static void UnbindRequest(string connectionKey, string requestId)
    {

        if (string.IsNullOrWhiteSpace(connectionKey) || string.IsNullOrWhiteSpace(requestId))
        {

            return;

        }

        ByRequest.Unbind(connectionKey, requestId);

    }

}
