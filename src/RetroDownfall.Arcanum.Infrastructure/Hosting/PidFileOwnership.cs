using System.ComponentModel;
using System.Diagnostics;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// What the operating system reports about the process a PID file names. <see cref="StartTime"/> is null
/// when the process exists but its start time cannot be read (another user's process, a restricted token).
/// </summary>
internal readonly record struct PidFileOwnerProcess(DateTimeOffset? StartTime);

/// <summary>
/// Decides whether the process a PID file names is still the Arcanum host that wrote it, or a different
/// process that was handed the same recycled id. Shared by <c>PidFileService</c> (which refuses to start
/// over a live owner) and <c>arcanum doctor</c> (which only repairs a file whose owner is gone), so the two
/// can never disagree about the same file.
/// </summary>
internal static class PidFileOwnership
{
    /// <summary>
    /// Slack for file-system timestamp granularity: the owner writes the file at the end of its own start-up,
    /// so its start time can only trail the file's last write by rounding.
    /// </summary>
    internal static readonly TimeSpan ClockTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads the live process for <paramref name="pid"/>, or null when no such process is running. A process
    /// that exists but that the caller is not allowed to open (a protected or other-session process, which on
    /// Windows answers <see cref="Win32Exception"/> "Access is denied" even to <c>HasExited</c>) is reported with
    /// no start time, so it counts as live instead of failing the caller.
    /// </summary>
    internal static PidFileOwnerProcess? LookUp(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);

            return Observe(() => process.HasExited, () => process.StartTime);
        }
        catch (Win32Exception)
        {
            return new PidFileOwnerProcess(null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Classifies the two operating-system reads <see cref="LookUp"/> makes on a process, so the answer to each
    /// failure can be pinned without a real process that refuses to be opened.
    /// </summary>
    internal static PidFileOwnerProcess? Observe(Func<bool> hasExited, Func<DateTime> startTime)
    {
        try
        {
            if (hasExited())
            {
                return null;
            }
        }
        catch (Win32Exception)
        {
            return new PidFileOwnerProcess(null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        try
        {
            return new PidFileOwnerProcess(new DateTimeOffset(startTime().ToUniversalTime()));
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return new PidFileOwnerProcess(null);
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> names a running process that could be the owner of a PID file last
    /// written at <paramref name="pidFileWrittenAt"/>. A process that started after the file was written
    /// cannot have written it, so it is a recycled id; a start time that cannot be read counts as live,
    /// because starting a second host over a live one is the worse mistake.
    /// </summary>
    internal static bool IsLiveOwner(
        int pid,
        DateTimeOffset pidFileWrittenAt,
        Func<int, PidFileOwnerProcess?> lookUp)
    {
        if (lookUp(pid) is not { } process)
        {
            return false;
        }

        return process.StartTime is not { } startedAt
            || startedAt <= pidFileWrittenAt + ClockTolerance;
    }
}
