using System.Diagnostics;

using System.Globalization;

using RetroDownfall.Arcanum.Core.Cli;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Infrastructure.Diagnostics;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Diagnostics;

/// <summary>
/// <c>arcanum doctor</c> and a starting host apply the same ownership rule to the PID file, so a file the host would
/// replace as crash residue must not read as a live host, and the stale-PID repair must act on exactly the files the
/// host would clear. A recycled id is the case that differs from "the process is gone": the process exists, but it
/// started after the file was written, so it cannot be the host that wrote it.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class PidFileCheckTests : IDisposable
{
    private readonly ArcanumTestHomeScope _home = new("arcanum-pid-check-tests");

    public void Dispose() => _home.Dispose();

    private static string PidPath => Path.Combine(ArcanumPaths.GrimoireDirectory, "arcanum.pid");

    [Fact]
    public void A_pid_file_naming_a_running_process_that_started_after_it_was_written_reads_as_stale()
    {
        WritePidFile(global::System.Environment.ProcessId, writtenAt: ThisProcessStartedAt().AddHours(-1));

        PidFilePosture posture = PidFilePosture.Read();

        Assert.Equal(PidFileKind.Stale, posture.Kind);

        Assert.Equal(global::System.Environment.ProcessId, posture.Pid);
    }

    [Fact]
    public void A_pid_file_naming_a_running_process_written_after_it_started_reads_as_live()
    {
        WritePidFile(global::System.Environment.ProcessId, writtenAt: DateTimeOffset.UtcNow);

        Assert.Equal(PidFileKind.Live, PidFilePosture.Read().Kind);
    }

    [Fact]
    public void A_pid_file_naming_a_process_that_has_exited_reads_as_stale()
    {
        WritePidFile(ExitedProcessId(), writtenAt: DateTimeOffset.UtcNow);

        Assert.Equal(PidFileKind.Stale, PidFilePosture.Read().Kind);
    }

    [Fact]
    public void The_finding_for_a_recycled_id_says_the_id_was_reused_and_offers_the_repair()
    {
        WritePidFile(global::System.Environment.ProcessId, writtenAt: ThisProcessStartedAt().AddHours(-1));

        DoctorFinding finding = PidFileCheck.Inspect();

        Assert.Equal(DoctorOutcome.Degraded, finding.Outcome);

        Assert.Contains("reused", finding.Detail, StringComparison.Ordinal);

        Assert.NotEmpty(finding.Remedies ?? []);
    }

    [Fact]
    public void The_finding_for_a_malformed_file_says_the_host_replaces_it_not_that_it_refuses_to_start()
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(PidPath, "not-a-pid");

        DoctorFinding finding = PidFileCheck.Inspect();

        Assert.DoesNotContain("refuse to start", finding.Detail, StringComparison.Ordinal);

        Assert.Contains("replaces it on the next start", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_repair_removes_a_file_naming_a_recycled_id_and_leaves_a_live_owner_alone()
    {
        RemoveStalePidRepair repair = new();

        WritePidFile(global::System.Environment.ProcessId, writtenAt: ThisProcessStartedAt().AddHours(-1));

        DoctorRepairResult applied = await repair.ApplyAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.Applied, applied.State);

        Assert.False(File.Exists(PidPath));

        WritePidFile(global::System.Environment.ProcessId, writtenAt: DateTimeOffset.UtcNow);

        DoctorRepairResult refused = await repair.ApplyAsync(CancellationToken.None);

        Assert.Equal(DoctorRepairState.Skipped, refused.State);

        Assert.True(File.Exists(PidPath));
    }

    private static DateTimeOffset ThisProcessStartedAt()
    {
        using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();

        return new DateTimeOffset(self.StartTime.ToUniversalTime());
    }

    private static void WritePidFile(int pid, DateTimeOffset writtenAt)
    {
        Directory.CreateDirectory(ArcanumPaths.GrimoireDirectory);

        File.WriteAllText(PidPath, pid.ToString(CultureInfo.InvariantCulture));

        File.SetLastWriteTimeUtc(PidPath, writtenAt.UtcDateTime);
    }

    /// <summary>
    /// The id of a process that ran and has exited, so the file names something that is definitely not running.
    /// </summary>
    private static int ExitedProcessId()
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new("cmd.exe", ["/c", "exit", "0"])
            : new("/bin/sh", ["-c", "exit 0"]);

        start.UseShellExecute = false;

        start.CreateNoWindow = true;

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;

        int pid = process.Id;

        process.WaitForExit();

        return pid;
    }
}
