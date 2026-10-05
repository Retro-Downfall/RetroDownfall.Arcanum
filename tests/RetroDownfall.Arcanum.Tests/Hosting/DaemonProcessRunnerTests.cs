using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class DaemonProcessRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-daemon-runner-" + Guid.NewGuid().ToString("N"));

    public DaemonProcessRunnerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task HungChild_IsKilledAtTimeout()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh and sleep.");

        string pidFile = Path.Combine(_directory, "child.pid");

        // The deadline is generous on purpose: the shell has to start and record its pid inside it, or there is no
        // child to prove was killed, and a loaded machine can take far longer than a few hundred milliseconds.
        DaemonProcessRunner runner = new(TimeSpan.FromSeconds(3));

        Stopwatch elapsed = Stopwatch.StartNew();

        DaemonProcessOutcome outcome = await runner.RunAsync(
            "/bin/sh",
            ["-c", $"echo $$ > '{pidFile}'; exec sleep 60"],
            CancellationToken.None);

        elapsed.Stop();

        Assert.True(outcome.FatalError.HasValue);

        Assert.Equal(DaemonProcessRunner.TimeoutErrorCode, outcome.FatalError.Value.Code);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"The runner waited {elapsed.Elapsed}.");

        await AssertChildGoneAsync(pidFile);
    }

    [SkippableFact]
    public async Task Cancellation_KillsTheChildAndPropagates()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh and sleep.");

        string pidFile = Path.Combine(_directory, "cancelled.pid");

        DaemonProcessRunner runner = new(TimeSpan.FromSeconds(60));

        using CancellationTokenSource cancellation = new();

        // Cancel only once the child has recorded its pid, so the test never races the shell's start-up.
        Task canceller = Task.Run(
            async () =>
            {
                DateTime giveUp = DateTime.UtcNow.AddSeconds(30);

                while (!File.Exists(pidFile) && DateTime.UtcNow < giveUp)
                {
                    await Task.Delay(20);
                }

                await cancellation.CancelAsync();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(
                "/bin/sh",
                ["-c", $"echo $$ > '{pidFile}'; exec sleep 60"],
                cancellation.Token));

        await canceller;

        await AssertChildGoneAsync(pidFile);
    }

    /// <summary>
    /// The Windows counterpart of the two tests above: the kill-the-tree path is the platform-divergent part of the
    /// runner, and a shell-free child that outlives the deadline has to be stopped there too. PowerShell starts a
    /// grandchild, so this also proves the whole tree is killed, not only the process the runner started.
    /// </summary>
    [SkippableFact]
    public async Task Windows_HungChild_AndItsGrandchild_AreKilledAtTimeout()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Uses Windows PowerShell and ping.exe.");

        string pidFile = Path.Combine(_directory, "grandchild.pid");

        string powerShell = Path.Combine(
            global::System.Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

        // PowerShell has to start and launch ping before the deadline, so the deadline allows a cold start.
        DaemonProcessRunner runner = new(TimeSpan.FromSeconds(10));

        Stopwatch elapsed = Stopwatch.StartNew();

        DaemonProcessOutcome outcome = await runner.RunAsync(
            powerShell,
            [
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                $"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -PassThru -WindowStyle Hidden; "
                + $"Set-Content -Path '{pidFile}' -Value $p.Id; Wait-Process -Id $p.Id",
            ],
            CancellationToken.None);

        elapsed.Stop();

        Assert.True(outcome.FatalError.HasValue);

        Assert.Equal(DaemonProcessRunner.TimeoutErrorCode, outcome.FatalError.Value.Code);

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30), $"The runner waited {elapsed.Elapsed}.");

        await AssertChildGoneAsync(pidFile);
    }

    [SkippableFact]
    public async Task Output_beyond_the_cap_is_discarded_and_the_child_still_finishes()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh, head and tr.");

        // 300 KB on stdout and on stderr, more than the pipe buffer holds, so a reader that stopped at the cap
        // without draining would leave the child blocked until the deadline.
        DaemonProcessOutcome outcome = await DaemonProcessRunner.Default.RunAsync(
            "/bin/sh",
            ["-c", "head -c 300000 /dev/zero | tr '\\0' x; head -c 300000 /dev/zero | tr '\\0' y >&2; exit 4"],
            CancellationToken.None);

        Assert.Null(outcome.FatalError);

        Assert.Equal(4, outcome.ExitCode);

        Assert.Equal(
            DaemonProcessRunner.MaxCapturedBytes + DaemonProcessRunner.TruncationMarker.Length,
            outcome.StdOut.Length);

        Assert.EndsWith(DaemonProcessRunner.TruncationMarker, outcome.StdOut, StringComparison.Ordinal);

        Assert.Equal(
            DaemonProcessRunner.MaxCapturedBytes + DaemonProcessRunner.TruncationMarker.Length,
            outcome.StdErr.Length);

        Assert.EndsWith(DaemonProcessRunner.TruncationMarker, outcome.StdErr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Output_within_the_cap_is_returned_whole_without_a_marker()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh.");

        DaemonProcessOutcome outcome = await DaemonProcessRunner.Default.RunAsync(
            "/bin/sh",
            ["-c", "printf 'one two three'"],
            CancellationToken.None);

        Assert.Equal("one two three", outcome.StdOut);

        Assert.Equal(string.Empty, outcome.StdErr);
    }

    [Fact]
    public async Task MissingExecutable_ReturnsFatalErrorInsteadOfThrowing()
    {
        string missing = "arcanum-missing-" + Guid.NewGuid().ToString("N");

        DaemonProcessOutcome outcome = await DaemonProcessRunner.Default.RunAsync(
            missing,
            [],
            CancellationToken.None);

        Assert.True(outcome.FatalError.HasValue);

        Assert.Equal(DaemonProcessRunner.StartErrorCode, outcome.FatalError.Value.Code);

        Assert.Contains(missing, outcome.FatalError.Value.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task FinishedChild_ReturnsItsExitCodeAndStreams()
    {
        Skip.If(OperatingSystem.IsWindows(), "Uses /bin/sh.");

        DaemonProcessOutcome outcome = await DaemonProcessRunner.Default.RunAsync(
            "/bin/sh",
            ["-c", "echo out; echo err >&2; exit 7"],
            CancellationToken.None);

        Assert.Null(outcome.FatalError);

        Assert.Equal(7, outcome.ExitCode);

        Assert.Equal("out", outcome.StdOut.Trim());

        Assert.Equal("err", outcome.StdErr.Trim());
    }

    private static async Task AssertChildGoneAsync(string pidFile)
    {
        Assert.True(File.Exists(pidFile), "The child never recorded its pid.");

        int pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid))
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Child process {pid} is still running.");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);

            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
