using SystemProcess = System.Diagnostics.Process;

using System.Runtime.Versioning;

using RetroDownfall.Arcanum.Cli.Services;

namespace RetroDownfall.Arcanum.Tests.Cli;

/// <summary>
/// The launcher's spawn is otherwise exercised only through a fake. These run the real one against a
/// harmless child.
/// </summary>
public sealed class ServeProcessLauncherTests
{
    /// <summary>
    /// The launcher returns only the child's id and releases its process handle, and
    /// releasing a handle must never stop the child: the host it starts keeps running after the launching
    /// command has finished (DESIGN 4.4.1, host lifetime).
    /// </summary>
    [SkippableFact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_started_child_keeps_running_after_the_launcher_releases_its_handle()
    {
        Skip.If(OperatingSystem.IsWindows(), "The child is /bin/sleep.");

        ServeProcessLauncher launcher = new();

        StartedProcess started = await launcher.StartServeAsync(
            new ServeProcessStartOptions(
                "/bin/sleep",
                ["30"],
                new Dictionary<string, string>(),
                Path.GetTempPath()),
            CancellationToken.None);

        using SystemProcess child = SystemProcess.GetProcessById(started.ProcessId);

        try
        {
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();

            await child.WaitForExitAsync();
        }
    }
}
