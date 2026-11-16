namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Host-only scheduling ownership. It grants no disclosure, tool or spending authority.</summary>
internal interface IProviderWorkOwner
{
    bool IsActive { get; }
}

/// <summary>
/// Carries the producer's scheduling owner through awaited auxiliary work. Native background turns
/// clear it; only the explicit subagent handoff may carry it into a child turn. Detached producers
/// clear it at their entry point. No member is serialized or supplied by a request body.
/// </summary>
internal static class ProviderWorkAmbient
{
    private sealed record State(IProviderWorkOwner? Owner, bool AllowChild);

    private static readonly AsyncLocal<State?> Current = new();

    internal static IProviderWorkOwner? Owner => Current.Value?.Owner;

    internal static IProviderWorkOwner? ChildOwner => Current.Value is { AllowChild: true } state ? state.Owner : null;

    internal static IDisposable Push(IProviderWorkOwner? owner, bool allowChild = false)
    {
        State? previous = Current.Value;

        Current.Value = new(owner, allowChild);

        return new Scope(previous);
    }

    internal static IDisposable AllowChild() => Push(Owner, allowChild: true);

    private sealed class Scope(State? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            Current.Value = previous;
        }
    }
}
