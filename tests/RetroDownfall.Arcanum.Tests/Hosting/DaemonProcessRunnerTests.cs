using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Infrastructure.Hosting;

namespace RetroDownfall.Arcanum.Tests.Hosting;

public sealed class DaemonProcessRunnerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "arcanum-daemon-runner-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The runner's deadline in the Windows tree-kill test. The child has to start Windows PowerShell, launch ping and
    /// record its pid inside it, and on a loaded runner that has taken longer than ten seconds, so the deadline is
    /// many times a warm start. The test always waits it out, because the child is hung on purpose.
    /// </summary>
    private static readonly TimeSpan WindowsDeadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after its deadline the runner may take to return: the tree kill and the teardown, which are not
    /// part of the deadline. Relative to the deadline, so changing one never leaves the other as a stale constant.
    /// </summary>
    private static readonly TimeSpan ReturnMargin = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the warm-up may take. It is not a measured deadline; it only keeps a Windows PowerShell that never
    /// answers from hanging the suite.
    /// </summary>
    private static readonly TimeSpan WarmUpLimit = TimeSpan.FromMinutes(2);

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
    /// <remarks>
    /// The deadline is the thing under test, so the child must be up and have recorded its grandchild's pid
    /// before it, and a cold Windows PowerShell 5.1 can take longer than a short deadline to get that far. The
    /// test therefore runs the same cmdlets once, outside the measured run, so the runner's deadline covers a
    /// warm start, and gives that deadline <see cref="WindowsDeadline"/> of headroom. The bound on how long the
    /// runner takes to return is relative to the deadline, not a constant that has to be revised with it.
    /// </remarks>
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

        await WarmUpPowerShellAsync(powerShell, Path.Combine(_directory, "warm-up.pid"));

        DaemonProcessRunner runner = new(WindowsDeadline);

        Stopwatch elapsed = Stopwatch.StartNew();

        DaemonProcessOutcome outcome = await runner.RunAsync(
            powerShell,
            ["-NoProfile", "-NonInteractive", "-Command", RecordGrandchildPidScript(pidFile, pings: "120")],
            CancellationToken.None);

        elapsed.Stop();

        Assert.True(outcome.FatalError.HasValue);

        Assert.Equal(DaemonProcessRunner.TimeoutErrorCode, outcome.FatalError.Value.Code);

        Assert.True(
            elapsed.Elapsed < WindowsDeadline + ReturnMargin,
            $"The runner waited {elapsed.Elapsed} for a deadline of {WindowsDeadline}.");

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

    /// <summary>
    /// Starts a grandchild <c>ping.exe</c> that sends <paramref name="pings"/> echo requests, records its pid in
    /// <paramref name="pidFile"/> and waits for it, so the process the runner starts stays alive for as long as the
    /// grandchild does.
    /// </summary>
    private static string RecordGrandchildPidScript(string pidFile, string pings) =>
        $"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','{pings}','127.0.0.1' -PassThru -WindowStyle Hidden; "
        + $"Set-Content -Path '{pidFile}' -Value $p.Id; Wait-Process -Id $p.Id";

    /// <summary>
    /// Runs the cmdlets the measured child runs, with one echo request instead of a hundred and twenty, and waits
    /// for them. Windows PowerShell 5.1 loads its assemblies and has them scanned on first use, which is what a
    /// loaded runner makes slow, and none of that is behaviour of <see cref="DaemonProcessRunner"/>. The exit status
    /// is not read: the grandchild may already be gone when the script waits for it, and all that matters is that the
    /// engine, the cmdlets and <c>ping.exe</c> are in the file cache afterwards.
    /// </summary>
    private static async Task WarmUpPowerShellAsync(string powerShell, string pidFile)
    {
        ProcessStartInfo startInfo = new(
            powerShell,
            ["-NoProfile", "-NonInteractive", "-Command", RecordGrandchildPidScript(pidFile, pings: "1")])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using System.Diagnostics.Process warmUp = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows PowerShell did not start for its warm-up.");

        using CancellationTokenSource limit = new(WarmUpLimit);

        try
        {
            await warmUp.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            warmUp.Kill(entireProcessTree: true);

            Assert.Fail($"Windows PowerShell did not finish its warm-up within {WarmUpLimit}.");
        }
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
