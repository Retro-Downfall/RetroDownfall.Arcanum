using System.ComponentModel;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class PidFileOwnershipTests
{
    private static readonly DateTime StartedAt = new(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_process_that_has_exited_is_not_running()
    {
        Assert.Null(PidFileOwnership.Observe(static () => true, static () => StartedAt));
    }

    [Fact]
    public void A_running_process_reports_its_start_time()
    {
        PidFileOwnerProcess? process = PidFileOwnership.Observe(static () => false, static () => StartedAt);

        Assert.Equal(new DateTimeOffset(StartedAt), process?.StartTime);
    }

    [Fact]
    public void A_process_the_caller_cannot_open_to_ask_whether_it_exited_counts_as_live()
    {
        // Windows: HasExited on a protected or other-session process throws Win32Exception "Access is denied".
        PidFileOwnerProcess? process = PidFileOwnership.Observe(
            static () => throw new Win32Exception(5, "Access is denied."),
            static () => StartedAt);

        Assert.NotNull(process);

        Assert.Null(process.Value.StartTime);
    }

    [Fact]
    public void A_process_whose_start_time_cannot_be_read_counts_as_live()
    {
        PidFileOwnerProcess? process = PidFileOwnership.Observe(
            static () => false,
            static () => throw new Win32Exception(5, "Access is denied."));

        Assert.NotNull(process);

        Assert.Null(process.Value.StartTime);
    }

    [Fact]
    public void A_process_that_vanishes_while_it_is_asked_whether_it_exited_is_not_running()
    {
        Assert.Null(
            PidFileOwnership.Observe(
                static () => throw new InvalidOperationException("No process is associated with this object."),
                static () => StartedAt));
    }

    [Fact]
    public void An_unreadable_owner_makes_the_pid_file_live_and_a_dead_one_does_not()
    {
        DateTimeOffset writtenAt = new(StartedAt);

        Assert.True(
            PidFileOwnership.IsLiveOwner(
                4242,
                writtenAt,
                static _ => PidFileOwnership.Observe(
                    static () => throw new Win32Exception(5, "Access is denied."),
                    static () => StartedAt)));

        Assert.False(
            PidFileOwnership.IsLiveOwner(
                4242,
                writtenAt,
                static _ => PidFileOwnership.Observe(static () => true, static () => StartedAt)));
    }

    [Fact]
    public void Look_up_finds_this_process_and_not_an_id_no_process_has()
    {
        PidFileOwnerProcess? self = PidFileOwnership.LookUp(global::System.Environment.ProcessId);

        Assert.NotNull(self);

        Assert.NotNull(self.Value.StartTime);

        Assert.Null(PidFileOwnership.LookUp(int.MaxValue));
    }

    [SkippableFact]
    public void Windows_look_up_answers_for_the_system_process_whether_or_not_it_can_be_opened()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Exercises the real Windows process-handle access path.");

        // Process id 4 is the protected System process: an unelevated caller cannot open it.
        Assert.NotNull(PidFileOwnership.LookUp(4));
    }
}
