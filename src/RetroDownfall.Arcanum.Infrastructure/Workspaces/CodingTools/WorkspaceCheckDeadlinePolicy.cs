using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Infrastructure.Workspaces.CodingTools;

internal sealed record WorkspaceCheckDeadlineAdmission(
    bool CanSpawn,
    string? Code,
    TimeSpan Remaining,
    TimeSpan Required);

internal sealed class WorkspaceCheckDeadlineBudget(
    TimeProvider timeProvider,
    long processDeadlineTimestamp,
    long absoluteDeadlineTimestamp)
{
    internal long AbsoluteDeadlineTimestamp { get; } =
        absoluteDeadlineTimestamp;

    internal TimeSpan GetPreflightTimeout(TimeSpan maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximum,
            TimeSpan.Zero);

        TimeSpan remaining = GetRemainingProcessTime();
        return remaining <= maximum ? remaining : maximum;
    }

    internal TimeSpan GetRemainingProcessTime() =>
        GetRemaining(processDeadlineTimestamp);

    internal TimeSpan GetRemainingTotalTime() =>
        GetRemaining(AbsoluteDeadlineTimestamp);

    private TimeSpan GetRemaining(long deadline)
    {
        long now = timeProvider.GetTimestamp();

        return deadline <= now
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                (deadline - now)
                / (double)timeProvider.TimestampFrequency);
    }
}

internal static class WorkspaceCheckDeadlinePolicy
{
    private static readonly TimeSpan McpCompletionGrace =
        TimeSpan.FromSeconds(1);

    internal static WorkspaceCheckDeadlineAdmission Evaluate(
        TimeProvider timeProvider,
        long inferenceDeadlineTimestamp,
        TimeSpan processTimeout)
    {

        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(processTimeout, TimeSpan.Zero);

        long now = timeProvider.GetTimestamp();
        TimeSpan remaining = inferenceDeadlineTimestamp <= now
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(
                (inferenceDeadlineTimestamp - now)
                / (double)timeProvider.TimestampFrequency);
        TimeSpan required = processTimeout
            + TimeSpan.FromSeconds(
                ArcanumSettingClamps.WorkspaceCheckCleanupGraceSeconds);

        return remaining >= required
            ? new WorkspaceCheckDeadlineAdmission(true, null, remaining, required)
            : new WorkspaceCheckDeadlineAdmission(
                false,
                "insufficient_deadline",
                remaining,
                required);
    }

    internal static WorkspaceCheckDeadlineBudget CreateBudget(
        TimeProvider timeProvider,
        long inferenceDeadlineTimestamp,
        TimeSpan processTimeout)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            processTimeout,
            TimeSpan.Zero);

        long now = timeProvider.GetTimestamp();
        TimeSpan cleanupReserve = TimeSpan.FromSeconds(
            ArcanumSettingClamps.WorkspaceCheckCleanupGraceSeconds);
        long requestedAbsolute = Add(
            timeProvider,
            now,
            processTimeout + cleanupReserve);
        long absolute = Math.Min(
            inferenceDeadlineTimestamp,
            requestedAbsolute);
        long processDeadline = Subtract(
            timeProvider,
            absolute,
            cleanupReserve);

        return new WorkspaceCheckDeadlineBudget(
            timeProvider,
            processDeadline,
            absolute);
    }

    internal static TimeSpan GetMcpRequestTimeout(
        TimeSpan processTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            processTimeout,
            TimeSpan.Zero);

        return processTimeout
            + TimeSpan.FromSeconds(
                ArcanumSettingClamps.WorkspaceCheckCleanupGraceSeconds)
            + McpCompletionGrace;
    }

    private static long Add(
        TimeProvider timeProvider,
        long timestamp,
        TimeSpan duration)
    {
        double timestampDelta =
            duration.TotalSeconds
            * timeProvider.TimestampFrequency;
        long delta = timestampDelta >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(timestampDelta);

        return timestamp > long.MaxValue - delta
            ? long.MaxValue
            : timestamp + delta;
    }

    private static long Subtract(
        TimeProvider timeProvider,
        long timestamp,
        TimeSpan duration)
    {
        double timestampDelta =
            duration.TotalSeconds
            * timeProvider.TimestampFrequency;
        long delta = timestampDelta >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(timestampDelta);

        return timestamp < long.MinValue + delta
            ? long.MinValue
            : timestamp - delta;
    }
}
