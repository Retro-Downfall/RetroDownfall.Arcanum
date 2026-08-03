namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

/// <summary>
/// Async-flow-local child execution context for isolated and eventually parallel child runs.
/// </summary>
internal static class SubagentExecutionAmbient
{
    private static readonly AsyncLocal<State?> Slot = new();

    public static bool IsIsolated => Slot.Value?.IsIsolated == true;

    public static DelegatedManaTracker? Tracker => Slot.Value?.Tracker;

    public static IDisposable EnterChild(DelegatedManaTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        State? previous = Slot.Value;

        Slot.Value = new State(IsIsolated: true, tracker);

        return new Scope(previous);
    }

    private sealed record State(
        bool IsIsolated,
        DelegatedManaTracker Tracker);

    private sealed class Scope(State? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Slot.Value = previous;
            }
        }
    }
}
