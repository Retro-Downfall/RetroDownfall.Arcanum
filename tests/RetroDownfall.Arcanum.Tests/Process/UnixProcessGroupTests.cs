using System.Diagnostics;
using System.Runtime.InteropServices;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Why the runner never tries to put an already-started child into a process group of its own: POSIX
/// refuses <c>setpgid</c> on a child that has executed an <c>exec</c>, and <c>Process.Start</c> returns
/// only after the exec. A group the runner can kill therefore has to be established by the launcher
/// (Linux <c>setsid --</c>); on macOS the runner has none. A cancellation or timeout there is the tree kill
/// (SIGKILL, so the launcher's EXIT trap never runs for it) and the descendant supervisor; the launcher's
/// trap covers only the child's own exit.
/// </summary>
[Collection("ChildProcess")]
public sealed class UnixProcessGroupTests
{
    private const int AccessDenied = 13;

    /// <summary>
    /// The premise above is a property of the kernel, so the test below proves it and this one holds the
    /// product to it: no authored source may call <c>setpgid</c>. A post-start fallback that is added back
    /// would fail on every run (the child has always exec'd by then) and, being best effort, fail silently,
    /// so nothing else in the suite would notice it.
    /// </summary>
    [Fact]
    public void No_production_code_calls_setpgid()
    {
        string[] callers =
        [
            .. ProductionSourceInventory.Sources()
                .Where(static source => source.Names("setpgid"))
                .Select(static source => source.RelativePath),
        ];

        Assert.True(
            callers.Length == 0,
            "A post-start setpgid can never succeed (EACCES after exec); the launcher must establish the group. Found in: "
            + string.Join(", ", callers));
    }

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
