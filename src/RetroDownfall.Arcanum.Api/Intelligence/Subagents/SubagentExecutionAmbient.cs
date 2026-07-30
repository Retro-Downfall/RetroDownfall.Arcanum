namespace RetroDownfall.Arcanum.Api.Intelligence.Subagents;

public sealed class SubagentDepthExceededException : Exception
{
    public SubagentDepthExceededException(int maxDepth)
        : base($"Maximum subagent depth ({maxDepth}) reached.")
    {
        MaxDepth = maxDepth;
    }

    public int MaxDepth { get; }
}

/// <summary>
/// Async-flow-local child execution context. It prevents recursive delegation without
/// coupling independent or eventually parallel child runs.
/// </summary>
internal static class SubagentExecutionAmbient
{
    private static readonly AsyncLocal<State?> Slot = new();

    public const int MaxSubagentDepth = 1;

    public static int Depth => Slot.Value?.Depth ?? 0;

    public static bool CanDelegate => Depth < MaxSubagentDepth;

    public static bool IsIsolated => Slot.Value?.IsIsolated == true;

    public static DelegatedManaTracker? Tracker => Slot.Value?.Tracker;

    public static IDisposable EnterChild(DelegatedManaTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        State? previous = Slot.Value;
        int nextDepth = (previous?.Depth ?? 0) + 1;

        if (nextDepth > MaxSubagentDepth)
        {
            throw new SubagentDepthExceededException(MaxSubagentDepth);
        }

        Slot.Value = new State(nextDepth, IsIsolated: true, tracker);

        return new Scope(previous);
    }

    private sealed record State(
        int Depth,
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
