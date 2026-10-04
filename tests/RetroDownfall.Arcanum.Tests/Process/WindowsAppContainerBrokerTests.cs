using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// End-to-end smoke for the Windows AppContainer broker: a bare command through
/// <see cref="CappedChildProcessRunner"/> with <see cref="ChildProcessSandboxRoots.ForExecuteCommand"/>
/// roots and default Sanctum limits, so the Job Object, host confirmation, profile creation, ACL grant,
/// <c>CreateProcessW</c> and restore all run for real. The xunit host cannot broker (re-executing it
/// would start the test platform), so the broker is the published apphost named by
/// <c>ARCANUM_PUBLISHED_EXECUTABLE</c>. Before this existed nothing exercised the success path, and the
/// broker could not have run a single command on Windows.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class WindowsAppContainerBrokerTests : IDisposable
{
    private const string PublishedExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    private const string AppContainerMappings =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings";

    private const string ProfilePrefix = "RetroDownfall.Arcanum.Tool.";

    private readonly string _workspace;

    public WindowsAppContainerBrokerTests()
    {
        _workspace = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-broker-ws-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Broker_runs_a_bare_command_inside_the_jail_and_restores_the_workspace_dacl()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer broker is Windows-only.");

        string executable = global::System.Environment.GetEnvironmentVariable(PublishedExecutableVariable) ?? string.Empty;

        Skip.If(
            string.IsNullOrWhiteSpace(executable),
            $"Set {PublishedExecutableVariable} to a published arcanum.exe; the xunit host cannot act as the broker.");

        Assert.True(File.Exists(executable), $"Published executable does not exist: {executable}");

        string daclBefore = Sddl(_workspace);
        int profilesBefore = ArcanumProfileCount();

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workspace,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("echo");
        startInfo.ArgumentList.Add("broker-ok");

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: true);

        CappedChildProcessRunResult result;
        using (ChildProcessFilesystemJail.UseWindowsBrokerExecutableForTests(executable))
        {
            result = await CappedChildProcessRunner.RunAsync(
                startInfo,
                ChildProcessEnvironmentProfile.ToolExec,
                totalOutputCapBytes: 1024 * 1024,
                TimeSpan.FromSeconds(60),
                new ResourceLimits(),
                new ProcessResourceLimiter(),
                CancellationToken.None,
                request,
                NullLogger.Instance);
        }

        Assert.True(
            result.Outcome == CappedChildProcessOutcome.Completed,
            $"Outcome {result.Outcome}; exit {result.ExitCode}; stderr: {result.Stderr.Text}; detail: {result.FilesystemSandboxDenialMessage}");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("broker-ok", result.Stdout.Text, StringComparison.Ordinal);
        Assert.Equal(daclBefore, Sddl(_workspace));
        Assert.Equal(profilesBefore, ArcanumProfileCount());
    }

    [SupportedOSPlatform("windows")]
    private static string Sddl(string path) =>
        new DirectoryInfo(path)
            .GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [SupportedOSPlatform("windows")]
    private static int ArcanumProfileCount()
    {
        using RegistryKey? mappings = Registry.CurrentUser.OpenSubKey(AppContainerMappings);
        if (mappings is null)
        {
            return 0;
        }

        int count = 0;
        foreach (string sid in mappings.GetSubKeyNames())
        {
            using RegistryKey? mapping = mappings.OpenSubKey(sid);
            if (mapping?.GetValue("Moniker") is string moniker
                && moniker.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
