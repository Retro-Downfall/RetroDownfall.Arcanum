using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

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
/// <remarks>
/// A run that completes proves the target started, not that it started confined: a
/// <c>PROC_THREAD_ATTRIBUTE</c> key or payload that the OS accepts but that does not mark the child as
/// an AppContainer yields an ordinary medium-integrity process, and every assertion on its output would
/// still pass. The confinement test therefore asks the target to read and write a location the current
/// user owns outside every granted root; only a target inside the AppContainer is refused. The refusal is
/// recognised by what it leaves behind, never by its text, which is localised.
/// </remarks>
[Collection("ProcessEnvironment")]
public sealed class WindowsAppContainerBrokerTests : IDisposable
{
    private const string PublishedExecutableVariable = "ARCANUM_PUBLISHED_EXECUTABLE";

    private const string AppContainerMappings =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings";

    private const string ProfilePrefix = "RetroDownfall.Arcanum.Tool.";

    private const string AppContainerSidPrefix = "S-1-15-2-";

    private readonly string _workspace;

    private readonly string _outside;

    public WindowsAppContainerBrokerTests()
    {
        string unique = Guid.NewGuid().ToString("N");

        _workspace = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-broker-ws-" + unique)).FullName;

        // Only named here. The confinement test creates it, because the protected DACL that makes it
        // unreachable from an AppContainer can only be written on Windows.
        _outside = Path.Combine(Path.GetTempPath(), "arcanum-broker-outside-" + unique);
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_workspace, nameof(WindowsAppContainerBrokerTests));

        _ = TestDirectoryCleanup.TryDelete(_outside, nameof(WindowsAppContainerBrokerTests));
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Broker_runs_a_bare_command_inside_the_jail_and_restores_the_workspace_dacl()
    {
        string executable = RequirePublishedExecutable();

        string daclBefore = WindowsDaclText.Of(_workspace);

        int profilesBefore = ArcanumProfileCount();

        CappedChildProcessRunResult result = await RunBrokeredAsync(
            executable,
            new ResourceLimits(),
            "echo",
            "broker-ok");

        // One diagnostic for both assertions. A broker that refused its own setup exits with a code in
        // the 70s (WindowsAppContainerBrokerExit) and says why on the one stderr it shares with the
        // target, which an assertion on the bare exit code would discard; the captured stdout rides
        // along because the exit-code assertion fails before the stdout one can say what the target printed.
        string diagnostic = Describe(result);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, diagnostic);

        Assert.True(result.ExitCode == 0, diagnostic);

        Assert.Contains("broker-ok", result.Stdout.Text, StringComparison.Ordinal);

        Assert.Equal(daclBefore, WindowsDaclText.Of(_workspace));

        Assert.Equal(profilesBefore, ArcanumProfileCount());
    }

    /// <summary>
    /// The broker is the process assigned to the Job Object and it creates the target inside the same
    /// job, so a job limited to exactly the configured count left the target no slot when the count was
    /// one: <c>CreateProcessW</c> failed and every sandboxed command exited 73. The default count of ten
    /// in the smoke above hides that.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Broker_leaves_a_tool_tree_of_one_process_its_own_slot_in_the_job()
    {
        string executable = RequirePublishedExecutable();

        CappedChildProcessRunResult result = await RunBrokeredAsync(
            executable,
            new ResourceLimits(MaxProcessCount: 1),
            "echo",
            "broker-one-slot");

        string diagnostic = Describe(result);

        Assert.True(result.Outcome == CappedChildProcessOutcome.Completed, diagnostic);

        Assert.True(result.ExitCode == 0, diagnostic);

        Assert.Contains("broker-one-slot", result.Stdout.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Proves the target is confined, not merely started. The probe directory is granted to the current
    /// user alone and lies outside every granted root, so a medium-integrity child of the same user reads
    /// and writes it freely and an AppContainer child, whose token matches no entry of that DACL, does
    /// neither. A control inside the workspace shows the refusals are the boundary and not a command that
    /// fails whatever it is asked.
    /// </summary>
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public async Task Broker_confines_the_target_to_the_granted_roots()
    {
        string executable = RequirePublishedExecutable();

        string token = "outside-" + Guid.NewGuid().ToString("N");

        string marker = CreateUserOnlyProbeDirectory(token);

        string written = Path.Combine(_outside, "written.txt");

        CappedChildProcessRunResult read = await RunBrokeredAsync(
            executable,
            new ResourceLimits(),
            "type",
            marker);

        string readDiagnostic = Describe(read);

        Assert.True(read.Outcome == CappedChildProcessOutcome.Completed, readDiagnostic);

        Assert.True(read.ExitCode != 0, "The target read a file outside every granted root. " + readDiagnostic);

        Assert.DoesNotContain(token, read.Stdout.Text, StringComparison.Ordinal);

        CappedChildProcessRunResult write = await RunBrokeredAsync(
            executable,
            new ResourceLimits(),
            "echo",
            "x",
            ">",
            written);

        string writeDiagnostic = Describe(write);

        Assert.True(write.Outcome == CappedChildProcessOutcome.Completed, writeDiagnostic);

        Assert.False(File.Exists(written), "The target wrote a file outside every granted root. " + writeDiagnostic);

        string inside = Path.Combine(_workspace, "granted.txt");

        CappedChildProcessRunResult control = await RunBrokeredAsync(
            executable,
            new ResourceLimits(),
            "echo",
            "granted",
            ">",
            inside);

        string controlDiagnostic = Describe(control);

        Assert.True(control.Outcome == CappedChildProcessOutcome.Completed, controlDiagnostic);

        Assert.True(control.ExitCode == 0, controlDiagnostic);

        Assert.True(File.Exists(inside), "The target could not write inside the workspace it was granted. " + controlDiagnostic);

        Assert.Contains("granted", File.ReadAllText(inside), StringComparison.Ordinal);
    }

    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Broker_refusing_an_incomplete_payload_says_why_on_stderr()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer broker is Windows-only.");

        // Exit 70 used to be a bare number: the runner handed the model an exit code and no reason.
        // Runs in-process, so it needs no published apphost.
        using StringWriter error = new();

        int profilesBefore = ArcanumProfileCount();

        int exitCode = WindowsAppContainerLauncher.Run(
            new SandboxExecHelperPayload
            {
                Target = @"C:\Windows\System32\cmd.exe",
                WindowsProfileName = WindowsAppContainerPolicy.CreateProfileName(),
                WindowsRestoreJournalPath = Path.Combine(_workspace, "undo.journal"),
            },
            error);

        Assert.Equal(WindowsAppContainerBrokerExit.InvalidPayload, exitCode);

        Assert.StartsWith("sandbox-exec: ", error.ToString(), StringComparison.Ordinal);

        Assert.Contains("WindowsJobAssignedSignalPath", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(profilesBefore, ArcanumProfileCount());
    }

    private static string RequirePublishedExecutable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The AppContainer broker is Windows-only.");

        string executable = global::System.Environment.GetEnvironmentVariable(PublishedExecutableVariable) ?? string.Empty;

        Skip.If(
            string.IsNullOrWhiteSpace(executable),
            $"Set {PublishedExecutableVariable} to a published arcanum.exe; the xunit host cannot act as the broker.");

        Assert.True(File.Exists(executable), $"Published executable does not exist: {executable}");

        return executable;
    }

    /// <summary>
    /// Runs <c>cmd /c</c> with <paramref name="commandArguments"/> through the real broker, with the
    /// workspace as the one granted root.
    /// </summary>
    private async Task<CappedChildProcessRunResult> RunBrokeredAsync(
        string executable,
        ResourceLimits limits,
        params string[] commandArguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _workspace,
        };
        startInfo.ArgumentList.Add("/c");

        foreach (string argument in commandArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ChildProcessSandboxRequest request = ChildProcessSandboxRoots.ForExecuteCommand(
            _workspace,
            null,
            allowUnsandboxed: false,
            windowsPathBoundaryRequired: true);

        using (ChildProcessFilesystemJail.UseWindowsBrokerExecutableForTests(executable))
        {
            return await CappedChildProcessRunner.RunAsync(
                startInfo,
                ChildProcessEnvironmentProfile.ToolExec,
                totalOutputCapBytes: 1024 * 1024,
                TimeSpan.FromSeconds(60),
                limits,
                new ProcessResourceLimiter(),
                CancellationToken.None,
                request,
                NullLogger.Instance);
        }
    }

    private static string Describe(CappedChildProcessRunResult result) =>
        $"Outcome {result.Outcome}; exit {result.ExitCode}; stdout: {result.Stdout.Text}; stderr: {result.Stderr.Text}; detail: {result.FilesystemSandboxDenialMessage}";

    /// <summary>
    /// Creates the probe directory with a protected DACL that grants the current user alone, whatever
    /// the host's <c>TEMP</c> would have inherited, and returns a file in it that holds
    /// <paramref name="token"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private string CreateUserOnlyProbeDirectory(string token)
    {
        DirectoryInfo directory = Directory.CreateDirectory(_outside);

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;

        DirectorySecurity security = new();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        directory.SetAccessControl(security);

        // The precondition the probe rests on: no entry may name an AppContainer, in particular the
        // ALL APPLICATION PACKAGES groups, or the target would reach the file through that group.
        foreach (AuthorizationRule rule in directory
            .GetAccessControl(AccessControlSections.Access)
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            Assert.DoesNotContain(AppContainerSidPrefix, rule.IdentityReference.Value, StringComparison.Ordinal);
        }

        string marker = Path.Combine(_outside, "marker.txt");

        File.WriteAllText(marker, token);

        return marker;
    }

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
