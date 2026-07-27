namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Host-owned monotonic inference deadline propagated to the internal MCP send boundary.
/// It is never accepted from model arguments.
/// </summary>
public static class WorkspaceCheckInferenceDeadlineAmbient
{

    private static readonly AsyncLocal<long?> Current = new();

    public static long? CurrentDeadlineTimestamp => Current.Value;

    public static IDisposable Begin(
        TimeProvider timeProvider,
        TimeSpan remaining)
    {

        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            remaining,
            TimeSpan.Zero);

        long previous = Current.Value ?? long.MinValue;
        double delta = remaining.TotalSeconds
            * timeProvider.TimestampFrequency;
        long now = timeProvider.GetTimestamp();
        long deadline = delta >= long.MaxValue - now
            ? long.MaxValue
            : now + (long)Math.Ceiling(delta);
        Current.Value = deadline;

        return new Scope(
            previous == long.MinValue ? null : previous);
    }

    public static IDisposable BeginAtTimestamp(long deadlineTimestamp)
    {

        long? previous = Current.Value;
        Current.Value = deadlineTimestamp;
        return new Scope(previous);
    }

    private sealed class Scope(long? previous) : IDisposable
    {

        private bool _disposed;

        public void Dispose()
        {

            if (_disposed)
            {

                return;
            }

            _disposed = true;
            Current.Value = previous;
        }
    }
}
