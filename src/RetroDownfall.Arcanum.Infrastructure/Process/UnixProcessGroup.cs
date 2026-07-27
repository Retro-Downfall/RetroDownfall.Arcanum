using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Best-effort Unix process-group ownership for bounded tool children. A captured group id remains
/// killable after the original parent exits, unlike <see cref="System.Diagnostics.Process.Kill(bool)"/>.
/// </summary>
internal static partial class UnixProcessGroup
{
    private const int SigKill = 9;

    private const int SigTerm = 15;

    internal static int? TryCreate(int processId)
    {
        if (OperatingSystem.IsWindows() || processId <= 0)
        {
            return null;
        }

        return SetProcessGroup(processId, processId) == 0
            ? processId
            : null;
    }

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

    [LibraryImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    private static partial int SetProcessGroup(int processId, int processGroupId);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}
