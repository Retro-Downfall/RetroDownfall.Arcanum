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

    internal static void TryTerminateAndKill(
        int? processGroupId)
    {
        if (OperatingSystem.IsWindows()
            || processGroupId is not int groupId
            || groupId <= 0)
        {
            return;
        }

        _ = Kill(-groupId, SigTerm);
        Thread.Sleep(100);
        _ = Kill(-groupId, SigKill);
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}
