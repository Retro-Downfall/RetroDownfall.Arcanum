using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Why the runner never tries to put an already-started child into a process group of its own: POSIX
/// refuses <c>setpgid</c> on a child that has executed an <c>exec</c>, and <c>Process.Start</c> returns
/// only after the exec. A group the runner can kill therefore has to be established by the launcher
/// (Linux <c>setsid --</c>); on macOS the runner has none and relies on the tree kill, the launcher's
/// own cleanup trap and the descendant supervisor.
/// </summary>
[Collection("ChildProcess")]
public sealed class UnixProcessGroupTests
{
    private const int AccessDenied = 13;

    [SkippableFact]
    public void Post_start_setpgid_on_an_exec_ed_child_is_refused()
    {
        Skip.If(OperatingSystem.IsWindows(), "Process groups are a Unix concept.");

        using System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sleep", "30")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _ = child.Start();

        try
        {
            int result = SetProcessGroup(child.Id, child.Id);

            int errno = Marshal.GetLastPInvokeError();

            Assert.Equal(-1, result);

            Assert.Equal(AccessDenied, errno);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    [DllImport("libc", EntryPoint = "setpgid", SetLastError = true)]
    private static extern int SetProcessGroup(int processId, int processGroupId);
}
