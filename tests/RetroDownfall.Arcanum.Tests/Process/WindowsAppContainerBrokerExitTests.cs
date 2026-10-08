using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// Every exit the Windows broker takes for its own failure writes one <c>sandbox-exec:</c> line to the
/// child's stderr, which the runner returns to the model with the exit code. Exits 70, 74 and 75 used to
/// return a bare number, and a launch the AppContainer could not read (a tool under ProgramData or on
/// another drive) surfaced only as a generic exit 73. The wording and the write are pinned here on every
/// host; the broker that calls them is Windows-only.
/// </summary>
public sealed class WindowsAppContainerBrokerExitTests
{
    [Fact]
    public void Each_broker_failure_writes_one_diagnostic_line_and_returns_its_exit_code()
    {
        (int ExitCode, string Message)[] failures =
        [
            (WindowsAppContainerBrokerExit.InvalidPayload, WindowsAppContainerBrokerExit.InvalidPayloadMessage("WindowsJobAssignedSignalPath")),
            (WindowsAppContainerBrokerExit.HostJobNotConfirmed, WindowsAppContainerJobAssignment.HostJobTimeoutMessage),
            (WindowsAppContainerBrokerExit.SetupFailed, WindowsAppContainerBrokerExit.LaunchFailedMessage(@"C:\ProgramData\chocolatey\bin\jq.exe", 5)),
            (WindowsAppContainerBrokerExit.ResumeFailed, WindowsAppContainerBrokerExit.ResumeFailedMessage(6)),
            (WindowsAppContainerBrokerExit.TargetExitCodeUnavailable, WindowsAppContainerBrokerExit.ExitCodeUnavailableMessage(6)),
        ];

        foreach ((int exitCode, string message) in failures)
        {
            using StringWriter error = new();

            int returned = WindowsAppContainerBrokerExit.Fail(error, exitCode, message);

            Assert.Equal(exitCode, returned);
            Assert.Equal("sandbox-exec: " + message + error.NewLine, error.ToString());
        }
    }

    [Fact]
    public void Broker_exit_codes_are_distinct_and_outside_the_range_a_tool_commonly_uses()
    {
        int[] codes =
        [
            WindowsAppContainerBrokerExit.InvalidPayload,
            WindowsAppContainerBrokerExit.HostJobNotConfirmed,
            WindowsAppContainerBrokerExit.ProfileCreationFailed,
            WindowsAppContainerBrokerExit.SetupFailed,
            WindowsAppContainerBrokerExit.ResumeFailed,
            WindowsAppContainerBrokerExit.TargetExitCodeUnavailable,
        ];

        Assert.Equal([70, 71, 72, 73, 74, 75], codes);
    }

    [Fact]
    public void Payload_without_a_member_the_broker_needs_names_that_member()
    {
        SandboxExecHelperPayload complete = new()
        {
            Target = @"C:\Windows\System32\cmd.exe",
            WindowsProfileName = "RetroDownfall.Arcanum.Tool.abc",
            WindowsRestoreJournalPath = @"C:\Temp\arcanum-win-acl.journal",
            WindowsJobAssignedSignalPath = @"C:\Temp\arcanum-win-job.signal",
        };

        Assert.Null(WindowsAppContainerBrokerExit.MissingPayloadMember(complete));
        Assert.Equal("Target", WindowsAppContainerBrokerExit.MissingPayloadMember(new SandboxExecHelperPayload()));
        Assert.Equal(
            "WindowsJobAssignedSignalPath",
            WindowsAppContainerBrokerExit.MissingPayloadMember(new SandboxExecHelperPayload
            {
                Target = complete.Target,
                WindowsProfileName = complete.WindowsProfileName,
                WindowsRestoreJournalPath = complete.WindowsRestoreJournalPath,
            }));

        string message = WindowsAppContainerBrokerExit.InvalidPayloadMessage("WindowsJobAssignedSignalPath");

        Assert.Contains("WindowsJobAssignedSignalPath", message, StringComparison.Ordinal);
        Assert.Contains("not started", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_and_exit_code_failures_say_what_happened_to_the_command()
    {
        string resume = WindowsAppContainerBrokerExit.ResumeFailedMessage(6);
        string exitCode = WindowsAppContainerBrokerExit.ExitCodeUnavailableMessage(6);

        // A target that was never resumed never ran; one whose exit code was lost did run, so its
        // effects may exist and the model must not assume either success or a clean refusal.
        Assert.Contains("resumed", resume, StringComparison.Ordinal);
        Assert.Contains("before it ran", resume, StringComparison.Ordinal);
        Assert.Contains("Win32 error 6", resume, StringComparison.Ordinal);
        Assert.Contains("exit code could not be read", exitCode, StringComparison.Ordinal);
        Assert.Contains("unknown", exitCode, StringComparison.Ordinal);
        Assert.Contains("Win32 error 6", exitCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_the_sandbox_cannot_read_names_the_directories_an_AppContainer_can_reach()
    {
        // Only Windows, Program Files, the run's granted roots and user-profile PATH directories are
        // readable to the per-run AppContainer. A tool under ProgramData or on another drive is refused
        // rather than granted, and the line says why instead of a bare "Access is denied".
        string denied = WindowsAppContainerBrokerExit.LaunchFailedMessage(@"D:\tools\fmt.exe", 5);
        string other = WindowsAppContainerBrokerExit.LaunchFailedMessage(@"C:\Windows\System32\cmd.exe", 193);

        Assert.Contains(@"D:\tools\fmt.exe", denied, StringComparison.Ordinal);
        Assert.Contains("ProgramData", denied, StringComparison.Ordinal);
        Assert.Contains("another drive", denied, StringComparison.Ordinal);
        Assert.Contains("Program Files", denied, StringComparison.Ordinal);
        Assert.Contains("not started", denied, StringComparison.Ordinal);
        Assert.Contains("Win32 error 193", other, StringComparison.Ordinal);
        Assert.Contains("not started", other, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_stderr_never_changes_the_exit_code()
    {
        int returned = WindowsAppContainerBrokerExit.Fail(
            new ThrowingWriter(),
            WindowsAppContainerBrokerExit.ResumeFailed,
            WindowsAppContainerBrokerExit.ResumeFailedMessage(6));

        Assert.Equal(WindowsAppContainerBrokerExit.ResumeFailed, returned);
    }

    private sealed class ThrowingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("stderr closed");
    }
}
