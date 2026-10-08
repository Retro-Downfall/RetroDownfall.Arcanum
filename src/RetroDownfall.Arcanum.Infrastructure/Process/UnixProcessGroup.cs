using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Best-effort Unix process-group termination for bounded tool children. A captured group id remains
/// killable after the original parent exits, unlike <see cref="System.Diagnostics.Process.Kill(bool)"/>.
/// </summary>
/// <remarks>
/// The group is always established by the launcher (<c>setsid --</c> on Linux, see
/// <see cref="UnixProcessGroupSupervisor"/>), never by the runner after the fact: <c>setpgid</c> on a child
/// that has executed an <c>exec</c> is refused with <c>EACCES</c>, and <c>Process.Start</c> returns only
/// after the exec, so a post-start attempt could never succeed.
/// </remarks>
internal static partial class UnixProcessGroup
{
    private const int SigKill = 9;

    private const int SigTerm = 15;

    private static readonly TimeSpan TerminationGrace = TimeSpan.FromMilliseconds(100);

    internal static void TryKill(int? processGroupId)
    {
        if (OperatingSystem.IsWindows()
            || processGroupId is not int groupId
            || groupId <= 0)
        {
            return;
        }

        _ = Kill(-groupId, SigKill);
    }

    /// <summary>
    /// SIGTERM, a 100 ms grace, then SIGKILL. The grace is awaited rather than slept: this runs inside
    /// cancellation callbacks, on the thread of whoever cancels the run (a request abort, the timeout timer),
    /// and a sleep there would hold that thread and every callback queued behind it. The SIGTERM is sent
    /// before the method first yields.
    /// </summary>
    internal static async Task TerminateAndKillAsync(
        int? processGroupId)
    {
        if (OperatingSystem.IsWindows()
            || processGroupId is not int groupId
            || groupId <= 0)
        {
            return;
        }

        _ = Kill(-groupId, SigTerm);

        await Task.Delay(TerminationGrace)
            .ConfigureAwait(false);

        _ = Kill(-groupId, SigKill);
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}
