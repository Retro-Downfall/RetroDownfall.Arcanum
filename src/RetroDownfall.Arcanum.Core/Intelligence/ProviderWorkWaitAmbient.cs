namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>Signals closure to an unsent background provider admission, without cancelling a dispatched effect.</summary>
internal static class ProviderWorkWaitAmbient
{
    private static readonly AsyncLocal<Scope?> Current = new();

    internal static CancellationToken CancellationToken => Live(Current.Value)?.Token ?? System.Threading.CancellationToken.None;

    internal static IDisposable Push(CancellationToken token)
    {
        Scope scope = new(token, Live(Current.Value));

        Current.Value = scope;

        return scope;
    }

    private static Scope? Live(Scope? scope)
    {
        while (scope is not null && !scope.Active)
        {
            scope = scope.Previous;
        }

        return scope;
    }

    private sealed class Scope(CancellationToken token, Scope? previous) : IDisposable
    {
        private int _active = 1;

        internal CancellationToken Token { get; } = token;

        internal Scope? Previous { get; } = previous;

        internal bool Active => Volatile.Read(ref _active) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _active, 0) == 0) return;

            if (ReferenceEquals(Current.Value, this)) Current.Value = Live(Previous);
        }
    }
}

/// <summary>An unsent background provider call yielded to an exclusive Grimoire closure.</summary>
internal sealed class ProviderWorkDeferredException(CancellationToken cancellationToken)
    : OperationCanceledException("Provider work was deferred for Grimoire maintenance.", cancellationToken);
