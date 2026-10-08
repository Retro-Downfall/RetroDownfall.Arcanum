using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The broker must not create the untrusted target until the host has put it in the run's own Job
/// Object. <c>IsProcessInJob(process, NULL)</c> answers "in any job", which is already true when the
/// host itself runs inside a job (a CI agent, a terminal multiplexer, a service wrapper), so the
/// broker would race ahead of the host's assignment. The host now confirms the assignment through an
/// owner-only signal file it writes only after <c>AssignProcessToJobObject</c> succeeded for this
/// broker, and the broker waits for both that confirmation and job membership.
/// </summary>
public sealed class WindowsAppContainerJobAssignmentTests : IDisposable
{
    private readonly string _root;

    public WindowsAppContainerJobAssignmentTests()
    {
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "arcanum-job-signal-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    public void Dispose()
    {
        _ = TestDirectoryCleanup.TryDelete(_root, nameof(WindowsAppContainerJobAssignmentTests));
    }

    [Fact]
    public void Broker_waits_for_the_host_job_not_an_inherited_one()
    {
        FakeClock clock = new();

        bool proceeded = WindowsAppContainerJobAssignment.WaitForHostAssignment(
            hostConfirmed: static () => false,
            inAnyJob: static () => true,
            TimeSpan.FromSeconds(5),
            clock.Now,
            clock.Advance);

        Assert.False(proceeded);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Broker_proceeds_once_the_host_confirms_its_job()
    {
        FakeClock clock = new();
        int polls = 0;

        bool proceeded = WindowsAppContainerJobAssignment.WaitForHostAssignment(
            hostConfirmed: () => ++polls >= 3,
            inAnyJob: static () => true,
            TimeSpan.FromSeconds(5),
            clock.Now,
            clock.Advance);

        Assert.True(proceeded);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Host_confirmation_without_job_membership_is_not_trusted()
    {
        FakeClock clock = new();

        bool proceeded = WindowsAppContainerJobAssignment.WaitForHostAssignment(
            hostConfirmed: static () => true,
            inAnyJob: static () => false,
            TimeSpan.FromSeconds(1),
            clock.Now,
            clock.Advance);

        Assert.False(proceeded);
    }

    [Fact]
    public void Signal_file_reads_confirmed_only_after_the_host_writes_it()
    {
        string signal = Path.Combine(_root, "job.signal");

        Assert.False(WindowsAppContainerJobAssignment.IsConfirmed(signal));

        File.WriteAllBytes(signal, []);

        Assert.False(WindowsAppContainerJobAssignment.IsConfirmed(signal));

        WindowsAppContainerJobAssignment.Confirm(signal);

        Assert.True(WindowsAppContainerJobAssignment.IsConfirmed(signal));
    }

    [Fact]
    public void Host_confirms_through_the_sandbox_result_and_ignores_runs_without_a_broker()
    {
        string signal = Path.Combine(_root, "job.signal");
        File.WriteAllBytes(signal, []);

        bool confirmed = ChildProcessFilesystemJail.ConfirmWindowsJobAssignment(
            new ChildProcessSandboxApplyResult
            {
                Status = ChildProcessSandboxApplyStatus.Applied,
                WindowsJobAssignedSignalPath = signal,
            });

        Assert.True(confirmed);
        Assert.True(WindowsAppContainerJobAssignment.IsConfirmed(signal));
        Assert.True(ChildProcessFilesystemJail.ConfirmWindowsJobAssignment(null));
        Assert.True(ChildProcessFilesystemJail.ConfirmWindowsJobAssignment(
            new ChildProcessSandboxApplyResult { Status = ChildProcessSandboxApplyStatus.Applied }));
    }

    [Fact]
    public void Timeout_message_names_the_job_object_and_says_nothing_ran()
    {
        Assert.Contains("Job Object", WindowsAppContainerJobAssignment.HostJobTimeoutMessage, StringComparison.Ordinal);
        Assert.Contains("not started", WindowsAppContainerJobAssignment.HostJobTimeoutMessage, StringComparison.Ordinal);
    }

    private sealed class FakeClock
    {
        private static readonly DateTime Start = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

        private DateTime _now = Start;

        internal TimeSpan Elapsed => _now - Start;

        internal DateTime Now() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }
}
