using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.ProcessExecution;

[Collection("ChildProcess")]
public sealed class CappedChildProcessRunnerTests
{
    private const string SentinelToken = "ARCANUM_RUNNER_TEST";

    [Fact]
    public async Task RunAsync_infinite_timeout_harmless_echo_returns_exit_code_zero()
    {
        ProcessStartInfo psi = CreateHarmlessEchoProcessStartInfo();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: Timeout.InfiniteTimeSpan,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Contains(SentinelToken, result.Stdout.Text, StringComparison.Ordinal);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_closes_child_stdin_so_a_reading_child_sees_eof_instead_of_the_host_console()
    {
        // RunAsync has no way to feed a child input, so every tool child must receive a closed pipe
        // on fd 0 rather than the host's inherited console. Without the redirect a child that reads
        // stdin (`cat`, `git commit`, `ssh`, `sudo`) blocks on the operator's terminal, and since
        // execute_command / run_spell_script / workspace_check all pass Timeout.InfiniteTimeSpan
        // there is no deadline that would ever unwedge the tool call.
        ProcessStartInfo psi = CreateStdinReadingProcessStartInfo();

        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(15),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        stopwatch.Stop();

        Assert.True(
            psi.RedirectStandardInput,
            "The runner must redirect child stdin so no tool child inherits the host's console handle.");

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Equal(0, result.ExitCode);

        Assert.Equal(SentinelToken, result.Stdout.Text.Trim());

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"A child reading stdin must see EOF immediately; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_truncates_stdout_when_exceeding_per_stream_cap()
    {
        ProcessStartInfo psi = CreateLargeOutputProcessStartInfo(payloadCharCount: 5000);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 2048,
            timeout: TimeSpan.FromSeconds(30),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.True(result.Stdout.Truncated);

        Assert.Equal(1024L, result.PerStreamCapBytes);
    }

    [Fact]
    public async Task RunAsync_spills_complete_stdout_after_preview_cap()
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-command-output-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(spillDirectory);

        try
        {
            const int payloadCharacters = 25_000;

            ProcessStartInfo psi = CreateLargeOutputProcessStartInfo(payloadCharacters);

            CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 2048,
                timeout: TimeSpan.FromSeconds(30),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None,
                outputSpillDirectory: spillDirectory);

            Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

            Assert.True(result.Stdout.Truncated);

            string spillPath = Assert.IsType<string>(result.Stdout.CompleteOutputPath);

            Assert.Equal(
                new string('x', payloadCharacters),
                (await File.ReadAllTextAsync(spillPath)).TrimEnd('\r', '\n'));

            Assert.Equal(
                new FileInfo(spillPath).Length,
                result.Stdout.TotalBytes);
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_cancellation_deletes_partial_spilled_output()
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-command-output-cancel-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(spillDirectory);

        try
        {
            ProcessStartInfo psi = CreateLargeOutputThenSleepProcessStartInfo(
                payloadCharCount: 500_000,
                seconds: 60);

            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(750));

            CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 2048,
                timeout: Timeout.InfiniteTimeSpan,
                resourceLimits: null,
                resourceLimiter: null,
                cancellation.Token,
                outputSpillDirectory: spillDirectory);

            Assert.Equal(CappedChildProcessOutcome.Canceled, result.Outcome);

            Assert.Empty(Directory.EnumerateFiles(spillDirectory));
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Abandoned_output_reader_spill_is_deleted_when_that_reader_completes()
    {
        // Cancellation/timeout paths give up on a stream reader after five seconds, which happens
        // when an orphaned descendant still holds the inherited pipe open. The reader keeps running
        // with its spill writer open, and it may not even have crossed the preview cap yet — so the
        // deletion the runner performs at that moment cannot be the only one, or the artifact
        // outlives the run inside the per-connection temporary root.
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-command-output-orphan-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(spillDirectory);

        try
        {
            string spillPath = Path.Combine(
                spillDirectory,
                "stdout-" + Guid.NewGuid().ToString("N") + ".utf8");

            TaskCompletionSource<CappedStreamOutput> abandonedReader = new(
                TaskCreationOptions.RunContinuationsAsynchronously);

            CappedChildProcessRunner.DeleteOutputSpillWhenReaderCompletes(
                abandonedReader.Task,
                spillPath);

            await File.WriteAllTextAsync(spillPath, "output written after the runner gave up");

            abandonedReader.SetResult(
                new CappedStreamOutput(
                    string.Empty,
                    Truncated: true,
                    spillPath,
                    TotalBytes: 39L));

            for (int attempt = 0; attempt < 100 && File.Exists(spillPath); attempt++)
            {
                await Task.Delay(50);
            }

            Assert.Empty(Directory.EnumerateFiles(spillDirectory));
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_spill_storage_failure_kills_process_instead_of_discarding_output()
    {
        string missingSpillDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-command-output-missing-test-" + Guid.NewGuid().ToString("N"),
            "not-created");

        ProcessStartInfo psi = CreateLargeOutputThenSleepProcessStartInfo(
            payloadCharCount: 500_000,
            seconds: 60);

        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 2048,
            timeout: Timeout.InfiniteTimeSpan,
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            outputSpillDirectory: missingSpillDirectory);

        stopwatch.Stop();

        Assert.Equal(
            CappedChildProcessOutcome.OutputPreservationFailed,
            result.Outcome);

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Expected output preservation failure to terminate the child promptly; took {stopwatch.Elapsed}.");

        Assert.False(Directory.Exists(missingSpillDirectory));
    }

    [Fact]
    public async Task RunAsync_spill_honors_existing_sanctum_file_write_policy()
    {
        string spillDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-command-output-budget-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(spillDirectory);

        try
        {
            ProcessStartInfo psi = CreateLargeOutputProcessStartInfo(payloadCharCount: 2_000_000);

            CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 2048,
                timeout: Timeout.InfiniteTimeSpan,
                resourceLimits: new ResourceLimits { MaxFileWriteMb = 1 },
                resourceLimiter: null,
                CancellationToken.None,
                outputSpillDirectory: spillDirectory);

            Assert.Equal(
                CappedChildProcessOutcome.OutputPreservationFailed,
                result.Outcome);

            Assert.Empty(Directory.EnumerateFiles(spillDirectory));

            Assert.IsType<CommandOutputSpillLimitException>(
                result.FaultException?.GetBaseException());
        }
        finally
        {
            Directory.Delete(spillDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_drains_output_past_cap_so_chatty_process_still_exits_promptly()
    {
        // A payload well beyond a typical OS pipe buffer (a few tens of KB): without continuing to
        // drain the pipe after the cap is hit, the child would block on its next write() once the
        // kernel pipe buffer fills, and RunAsync would only unblock via the timeout below — this
        // test's timeout is intentionally short so a regression here fails fast instead of hanging.
        ProcessStartInfo psi = CreateLargeOutputProcessStartInfo(payloadCharCount: 500_000);

        Stopwatch stopwatch = Stopwatch.StartNew();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 2048,
            timeout: TimeSpan.FromSeconds(30),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        stopwatch.Stop();

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.True(result.Stdout.Truncated);

        // Well under the timeout — proves the child exited on its own because the pipe kept
        // draining, rather than RunAsync only unblocking once the timeout killed the process tree.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Expected a prompt exit; took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_times_out_and_kills_long_running_process()
    {
        ProcessStartInfo psi = CreateSleepProcessStartInfo(seconds: 60);

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromMilliseconds(750),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.TimedOut, result.Outcome);
    }

    [SkippableFact]
    public void Unix_process_group_supervisor_without_setsid_uses_shell_fallback()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-specific launch construction.");
        ProcessStartInfo startInfo = new()
        {
            FileName = "/bin/echo",
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add("hello");

        bool directGroup = UnixProcessGroupSupervisor.Apply(
            startInfo,
            static () => null);

        Assert.False(directGroup);
        Assert.Equal("/bin/sh", startInfo.FileName);
        Assert.Equal("-c", startInfo.ArgumentList[0]);
        Assert.Contains("set -m", startInfo.ArgumentList[1], StringComparison.Ordinal);
        Assert.Equal("arcanum-process-group", startInfo.ArgumentList[2]);
        Assert.Equal("/bin/echo", startInfo.ArgumentList[3]);
        Assert.Equal("-n", startInfo.ArgumentList[4]);
        Assert.Equal("hello", startInfo.ArgumentList[5]);
    }

    [SkippableFact]
    public void Unix_process_group_supervisor_with_setsid_preserves_argv()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux-specific launch construction.");
        ProcessStartInfo startInfo = new()
        {
            FileName = "-option-shaped-command",
        };
        startInfo.ArgumentList.Add("argument with spaces");

        bool directGroup = UnixProcessGroupSupervisor.Apply(
            startInfo,
            static () => "/usr/bin/setsid");

        Assert.True(directGroup);
        Assert.Equal("/usr/bin/setsid", startInfo.FileName);
        Assert.Equal(
            ["--", "-option-shaped-command", "argument with spaces"],
            startInfo.ArgumentList);
    }

    [SkippableFact]
    public async Task RunAsync_kills_process_group_descendants_after_normal_parent_exit()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix process-group cleanup is covered on Unix hosts.");
        ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("sleep 60 </dev/null >/dev/null 2>&1 & echo $!; exit 0");

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);
        int descendantPid = int.Parse(
            result.Stdout.Text.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200);

        try
        {
            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(descendantPid);
            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(2)),
                $"Detached descendant {descendantPid} survived normal parent exit.");
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        finally
        {
            TestDescendantProcess.KillTreeIfRunning(descendantPid);
        }
    }

    [SkippableFact]
    public async Task RunAsync_kills_process_group_descendants_on_timeout()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix process-group cleanup is covered on Unix hosts.");
        ProcessStartInfo psi = new()
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(
            "sleep 60 </dev/null >/dev/null 2>&1 & echo $!; sleep 60");

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromMilliseconds(750),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);

        Assert.Equal(
            CappedChildProcessOutcome.TimedOut,
            result.Outcome);
        int descendantPid = int.Parse(
            result.Stdout.Text.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200);

        try
        {
            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(
                    descendantPid);
            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(2)),
                $"Detached descendant {descendantPid} survived timeout cleanup.");
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        finally
        {
            TestDescendantProcess.KillTreeIfRunning(descendantPid);
        }
    }

    [SkippableTheory]
    [InlineData("Process.setsid")]
    [InlineData("Process.setpgid(0, 0)")]
    public async Task RunAsync_kills_group_escaping_closed_pipe_descendant_after_parent_success(
        string escape)
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS(),
            "macOS descendant ancestry containment is platform-specific.");
        ProcessStartInfo psi = new()
        {
            FileName = "/usr/bin/ruby",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(
            $"fork {{ {escape}; puts Process.pid; STDOUT.flush; STDOUT.close; STDERR.close; sleep 15 }}; sleep 1.5; exit 0");

        Stopwatch stopwatch = Stopwatch.StartNew();
        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(10),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);
        stopwatch.Stop();

        Assert.Equal(
            CappedChildProcessOutcome.Completed,
            result.Outcome);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Containment cleanup took {stopwatch.Elapsed}.");
        int descendantPid = int.Parse(
            result.Stdout.Text.Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(200);

        try
        {
            using global::System.Diagnostics.Process descendant =
                global::System.Diagnostics.Process.GetProcessById(
                    descendantPid);
            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(2)),
                $"Escaped descendant {descendantPid} survived parent success.");
        }
        catch (ArgumentException)
        {
            // Process no longer exists.
        }
        finally
        {
            TestDescendantProcess.KillTreeIfRunning(descendantPid);
        }
    }

    [SkippableFact]
    public async Task RunAsync_abandons_the_post_exit_drain_when_a_descendant_holds_the_pipes_open()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS() && File.Exists("/usr/bin/ruby"),
            "Needs a POSIX host with fork/setsid available to strand an inherited pipe.");

        // Every containment sweep the runner performs before draining output is best-effort
        // (DESIGN §11.15). A descendant that double-forks away from its parent AND leaves the
        // captured process group AND keeps the inherited stdout/stderr open therefore survives them
        // all, and the readers never see EOF. The drain must give up on those readers instead of
        // blocking: the caller passes Timeout.InfiniteTimeSpan, and the Job Object / AppContainer
        // undo-log / sandbox temp-directory cleanup all live past the drain, so blocking here
        // wedges the tool call forever and leaks the run's OS resources for the host's lifetime.
        string pidDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-open-pipe-descendant-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(pidDirectory);

        string pidFile = Path.Combine(pidDirectory, "descendant.pid");

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "/usr/bin/ruby",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(
                "fork { fork { Process.setsid; File.write('"
                + pidFile
                + "', Process.pid.to_s); sleep 30 }; exit! 0 }; sleep 0.5; exit 0");

            Stopwatch stopwatch = Stopwatch.StartNew();

            Task<CappedChildProcessRunResult> run = CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: Timeout.InfiniteTimeSpan,
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);

            Task finished = await Task.WhenAny(
                run,
                Task.Delay(TimeSpan.FromSeconds(15)));

            Assert.True(
                ReferenceEquals(finished, run),
                "RunAsync never returned: the post-exit output drain is unbounded, so a descendant "
                + "holding the inherited pipe open hangs the tool call permanently.");

            CappedChildProcessRunResult result = await run;

            stopwatch.Stop();

            Assert.Equal(
                CappedChildProcessOutcome.Completed,
                result.Outcome);

            Assert.Equal(0, result.ExitCode);

            // Output the runner walked away from is reported as truncated rather than presented to
            // the model as the command's complete output.
            Assert.True(result.Stdout.Truncated);

            Assert.True(result.Stderr.Truncated);

            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(13),
                $"Abandoning the drain took {stopwatch.Elapsed}.");
        }
        finally
        {
            TestDescendantProcess.KillRecorded(pidFile);

            Directory.Delete(pidDirectory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task RunAsync_post_exit_drain_is_bounded_by_one_grace_when_both_pipes_are_held()
    {
        Skip.IfNot(
            OperatingSystem.IsMacOS() && File.Exists("/usr/bin/ruby"),
            "Needs a POSIX host with fork/setsid available to strand an inherited pipe.");

        // The post-exit drain documents one 5 s bound. Waiting for each pipe in turn made it up to twice
        // that: a descendant that closes stdout late but keeps stderr was given a fresh 5 s for stderr
        // once stdout finally reached EOF. Both pipes are held here — stdout for four seconds, stderr for
        // good. What proves the shared deadline is AwaitPostExitOutputAsync_gives_both_pipes_one_shared_
        // deadline, on a clock the test advances; this run only shows the whole path ends and reports each
        // pipe's own state, so its time bound is a hang detector, not a measurement (a measurement of
        // seconds is not stable on a host that is busy with other work).
        string pidDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-two-pipe-descendant-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(pidDirectory);

        string pidFile = Path.Combine(pidDirectory, "descendant.pid");

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "/usr/bin/ruby",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(
                "fork { fork { Process.setsid; File.write('"
                + pidFile
                + "', Process.pid.to_s); sleep 4; STDOUT.reopen('/dev/null'); sleep 30 }; exit! 0 }; sleep 0.5; exit 0");

            Stopwatch stopwatch = Stopwatch.StartNew();

            CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: Timeout.InfiniteTimeSpan,
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None);

            stopwatch.Stop();

            Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

            // (dup2 over fd 1 is what really closes the pipe's write end; Ruby's STDOUT.close does not.)
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"The post-exit drain took {stopwatch.Elapsed}; it should have given up after its 5 s deadline.");

            // The reader that did finish keeps its output; only the one still held is reported truncated.
            Assert.False(result.Stdout.Truncated);

            Assert.True(result.Stderr.Truncated);
        }
        finally
        {
            TestDescendantProcess.KillRecorded(pidFile);

            Directory.Delete(pidDirectory, recursive: true);
        }
    }

    /// <summary>
    /// The shared deadline, on a clock the test advances. Stdout reaches EOF four seconds in and stderr is
    /// held for good; the drain's one 5 s deadline lapses at five seconds on the clock. A drain that waited
    /// for each pipe in turn started stderr's wait only once stdout was done, so at five seconds it would
    /// still be waiting.
    /// </summary>
    [Fact]
    public async Task AwaitPostExitOutputAsync_gives_both_pipes_one_shared_deadline()
    {
        ManualTimerTimeProvider clock = new();

        TaskCompletionSource<CappedStreamOutput> stdout = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<CappedStreamOutput> stderr = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<(CappedStreamOutput Stdout, CappedStreamOutput Stderr)> drain =
            CappedChildProcessRunner.AwaitPostExitOutputAsync(
                stdout.Task,
                stderr.Task,
                TimeSpan.FromSeconds(5),
                clock);

        await clock.FirstTimerCreated.WaitAsync(TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(4));

        stdout.SetResult(new CappedStreamOutput("stdout", Truncated: false));

        // Let the thread pool run whatever the drain does once stdout completes (a drain that waits for
        // each pipe in turn arms stderr's own timer here). Correct code does not depend on it: the one
        // deadline is armed already and nothing waits on this. It only decides whether a regression to
        // the sequential waits is seen, so a slow host can miss that regression but never fail correct code.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.False(drain.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));

        // The real delay only runs when the deadline did not lapse, to turn a hang into a failure.
        Task finished = await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            ReferenceEquals(drain, finished),
            "The drain was still waiting at five seconds: stderr was given its own grace after stdout finished.");

        await Assert.ThrowsAsync<TimeoutException>(() => drain);
    }

    [Fact]
    public async Task AwaitPostExitOutputAsync_returns_both_outputs_and_leaves_no_timer_when_the_readers_finish_in_time()
    {
        ManualTimerTimeProvider clock = new();

        Task<CappedStreamOutput> stdout = Task.FromResult(new CappedStreamOutput("out", Truncated: false));

        Task<CappedStreamOutput> stderr = Task.FromResult(new CappedStreamOutput("err", Truncated: true));

        (CappedStreamOutput Stdout, CappedStreamOutput Stderr) outputs =
            await CappedChildProcessRunner.AwaitPostExitOutputAsync(stdout, stderr, TimeSpan.FromSeconds(5), clock);

        Assert.Equal("out", outputs.Stdout.Text);

        Assert.Equal("err", outputs.Stderr.Text);

        Assert.True(outputs.Stderr.Truncated);

        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task AwaitPostExitOutputAsync_surfaces_a_failed_reader_without_waiting_out_the_grace()
    {
        ManualTimerTimeProvider clock = new();

        TaskCompletionSource<CappedStreamOutput> stdout = new(TaskCreationOptions.RunContinuationsAsynchronously);

        TaskCompletionSource<CappedStreamOutput> stderr = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<(CappedStreamOutput Stdout, CappedStreamOutput Stderr)> drain =
            CappedChildProcessRunner.AwaitPostExitOutputAsync(
                stdout.Task,
                stderr.Task,
                TimeSpan.FromSeconds(5),
                clock);

        stderr.SetException(new IOException("the pipe broke"));

        // The clock never advances: a reader that already failed must not cost the grace.
        IOException failure = await Assert.ThrowsAsync<IOException>(() => drain.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("the pipe broke", failure.Message);
    }

    [SkippableFact]
    public async Task RunAsync_sandbox_unavailable_still_runs_limiter_cleanup()
    {
        int cleanups = 0;

        ProcessStartInfo psi = CreateHarmlessEchoProcessStartInfo();

        // No roots to allow is "no jail to apply" on every platform, and there is no operator escape, so
        // the runner refuses to start the child — after the limiter has already created its scope.
        ChildProcessSandboxRequest request = new()
        {
            ReadWriteRoots = [],

            ReadExecuteRoots = [],

            AllowUnsandboxed = false,

            ToolName = "execute_command",
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 256 },
            resourceLimiter: new CleanupRecordingLimiter(() =>
            {
                _ = Interlocked.Increment(ref cleanups);

                return Task.CompletedTask;
            }),
            CancellationToken.None,
            request);

        Assert.Equal(CappedChildProcessOutcome.FilesystemSandboxUnavailable, result.Outcome);

        // The cgroup scope directory (and any other limiter state) exists from Apply onwards, so every
        // return after it — not only the ones after Process.Start — owes the cleanup.
        Assert.Equal(1, cleanups);
    }

    [SkippableFact]
    public async Task RunAsync_a_faulting_limiter_cleanup_does_not_replace_the_result()
    {
        Skip.If(OperatingSystem.IsWindows(), "The harmless child is POSIX here.");

        ProcessStartInfo psi = CreateHarmlessEchoProcessStartInfo();

        // Teardown runs in a finally: an exception from one step there replaced the run's result and
        // skipped every step after it.
        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 256 },
            resourceLimiter: new CleanupRecordingLimiter(
                () => throw new InvalidOperationException("injected limiter cleanup fault")),
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Contains(SentinelToken, result.Stdout.Text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunAsync_kills_the_child_when_the_memory_monitor_cannot_attach()
    {
        Skip.If(OperatingSystem.IsWindows(), "The memory monitor is a macOS mechanism; the child here is POSIX.");

        int startedPid = 0;

        ProcessStartInfo psi = CreateSleepProcessStartInfo(30);

        // A configured ceiling with nothing to enforce it would let the child run unbounded, so the
        // runner must kill it and fail closed. The seam stands in for a supervisor that could not attach.
        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(60),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 256 },
            resourceLimiter: new MonitoredMemoryLimiter(256L * 1024 * 1024),
            CancellationToken.None,
            descendantSupervisorFactory: (pid, _) =>
            {
                startedPid = pid;

                return null;
            });

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitApplyFailed, result.Outcome);

        Assert.Contains("memory monitor could not attach", result.ResourceLimitApplyError, StringComparison.Ordinal);

        Assert.NotEqual(0, startedPid);

        Assert.True(
            await WaitForProcessExitOrZombieAsync(startedPid, TimeSpan.FromSeconds(5)),
            "The un-monitored child was left running.");
    }

    [SkippableFact]
    public async Task RunAsync_fails_the_run_when_the_memory_monitor_faults()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        ProcessStartInfo psi = CreateSleepProcessStartInfo(30);

        TestCapturingLogger<CappedChildProcessRunnerTests> logger = new();

        // The monitor loop is the only thing enforcing a macOS memory ceiling. When it faults the
        // supervisor ends the tree, and the run is reported as one whose monitor stopped — not as a normal
        // completion of a child whose limit silently stopped being enforced, and not as an apply failure
        // either: the child did start and run, so a caller must not describe it as never having run.
        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(60),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 4096 },
            resourceLimiter: new MonitoredMemoryLimiter(4096L * 1024 * 1024),
            CancellationToken.None,
            logger: logger,
            descendantSupervisorFactory: (pid, limit) => MacOsDescendantSupervisor.TryStart(
                pid,
                monitorTickHold: () => throw new InvalidOperationException("injected monitor fault"),
                memoryLimitBytes: limit));

        Assert.Equal(CappedChildProcessOutcome.MemoryMonitorStopped, result.Outcome);

        Assert.Null(result.ResourceLimitApplyError);

        Assert.IsType<InvalidOperationException>(result.FaultException);

        // Reported once, not once by the result builder and again by the teardown that logs every fault.
        Assert.Single(MonitorFaultErrors(logger));
    }

    /// <summary>
    /// A monitor that killed the tree for a breach and then faulted before the run ended has already
    /// recorded the real cause. Reporting the fault instead discarded that evidence: the caller was told
    /// the limit could not be enforced, when it had been enforced and had been exceeded.
    /// </summary>
    [SkippableFact]
    public async Task RunAsync_reports_the_memory_breach_when_the_monitor_faults_after_killing_the_tree()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        const long ceilingBytes = 64L * 1024 * 1024;

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateSleepProcessStartInfo(30),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(60),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 64 },
            resourceLimiter: new MonitoredMemoryLimiter(ceilingBytes),
            CancellationToken.None,
            descendantSupervisorFactory: (pid, limit) => MacOsDescendantSupervisor.TryStart(
                pid,
                // The first tick measures the root over the ceiling and kills the tree; the hold that runs
                // at the end of that same tick then faults the loop.
                monitorTickHold: () => throw new InvalidOperationException("injected monitor fault"),
                memoryLimitBytes: limit,
                footprintReader: _ => ceilingBytes * 2));

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitExceeded, result.Outcome);

        Assert.Equal(ResourceLimitKind.Memory, result.ExceededResource);
    }

    [SkippableFact]
    public async Task RunAsync_logs_a_faulted_monitor_when_no_memory_ceiling_is_configured()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        TestCapturingLogger<CappedChildProcessRunnerTests> logger = new();

        // Without a ceiling nothing is killed and the run still completes, but the loop that tracks
        // descendants before they reparent (the workspace_check containment boundary) has stopped, which
        // an operator must be able to see.
        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateSleepProcessStartInfo(1),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(60),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            logger: logger,
            descendantSupervisorFactory: FaultingMonitorFactory);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Single(MonitorFaultErrors(logger));
    }

    [SkippableFact]
    public async Task RunAsync_logs_a_faulted_monitor_when_the_run_is_canceled()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        TestCapturingLogger<CappedChildProcessRunnerTests> logger = new();

        using CancellationTokenSource cancellation = new();

        cancellation.CancelAfter(TimeSpan.FromSeconds(2));

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateSleepProcessStartInfo(30),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: Timeout.InfiniteTimeSpan,
            resourceLimits: null,
            resourceLimiter: null,
            cancellation.Token,
            logger: logger,
            descendantSupervisorFactory: FaultingMonitorFactory);

        Assert.Equal(CappedChildProcessOutcome.Canceled, result.Outcome);

        Assert.Single(MonitorFaultErrors(logger));
    }

    [SkippableFact]
    public async Task RunAsync_logs_a_faulted_monitor_when_the_run_times_out()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        TestCapturingLogger<CappedChildProcessRunnerTests> logger = new();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateSleepProcessStartInfo(30),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(2),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            logger: logger,
            descendantSupervisorFactory: FaultingMonitorFactory);

        Assert.Equal(CappedChildProcessOutcome.TimedOut, result.Outcome);

        Assert.Single(MonitorFaultErrors(logger));
    }

    /// <summary>
    /// A descendant left out of the memory ceiling's sum is a gap in that ceiling whichever way the run
    /// ends. The warning was written only on the normal-exit path, so a run that timed out — the likeliest
    /// end for a tree the ceiling was not fully covering — reported nothing.
    /// </summary>
    [SkippableFact]
    public async Task RunAsync_logs_unreadable_descendant_footprints_when_the_run_times_out()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        TestCapturingLogger<CappedChildProcessRunnerTests> logger = new();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateBackgroundedSleepProcessStartInfo(30),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(2),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 4096 },
            resourceLimiter: new MonitoredMemoryLimiter(4096L * 1024 * 1024),
            CancellationToken.None,
            logger: logger,
            descendantSupervisorFactory: RootOnlyFootprintMonitorFactory);

        Assert.Equal(CappedChildProcessOutcome.TimedOut, result.Outcome);

        Assert.Single(UnreadableFootprintWarnings(logger));
    }

    [SkippableFact]
    public async Task RunAsync_logs_unreadable_descendant_footprints_once_when_the_run_completes()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(), "The descendant supervisor is a macOS primitive.");

        TestCapturingLogger<CappedChildProcessRunnerTests> logger = new();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            CreateBackgroundedSleepProcessStartInfo(1),
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(60),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 4096 },
            resourceLimiter: new MonitoredMemoryLimiter(4096L * 1024 * 1024),
            CancellationToken.None,
            logger: logger,
            descendantSupervisorFactory: RootOnlyFootprintMonitorFactory);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Single(UnreadableFootprintWarnings(logger));
    }

    /// <summary>A monitor that can read only the root's footprint, so every descendant is unreadable.</summary>
    private static MacOsDescendantSupervisor? RootOnlyFootprintMonitorFactory(int pid, long? memoryLimitBytes) =>
        MacOsDescendantSupervisor.TryStart(
            pid,
            memoryLimitBytes: memoryLimitBytes,
            footprintReader: readPid => readPid == pid ? 1L : null);

    private static TestLogEntry[] UnreadableFootprintWarnings(TestCapturingLogger<CappedChildProcessRunnerTests> logger) =>
        [.. logger.Entries.Where(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains("could not read the footprint", StringComparison.Ordinal))];

    private static ProcessStartInfo CreateBackgroundedSleepProcessStartInfo(int seconds) => new()
    {
        FileName = "/bin/sh",

        RedirectStandardOutput = true,

        RedirectStandardError = true,

        UseShellExecute = false,

        ArgumentList = { "-c", $"sleep {seconds} </dev/null >/dev/null 2>&1 & wait" },
    };

    private static MacOsDescendantSupervisor? FaultingMonitorFactory(int pid, long? memoryLimitBytes) =>
        MacOsDescendantSupervisor.TryStart(
            pid,
            monitorTickHold: () => throw new InvalidOperationException("injected monitor fault"),
            memoryLimitBytes: memoryLimitBytes);

    private static TestLogEntry[] MonitorFaultErrors(TestCapturingLogger<CappedChildProcessRunnerTests> logger) =>
        [.. logger.Entries.Where(entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is InvalidOperationException { Message: "injected monitor fault" })];

    [SkippableFact]
    public async Task RunAsync_cancellation_kills_a_forked_descendant_without_the_descendant_supervisor()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix process cleanup is covered on Unix hosts.");

        string pidDirectory = Path.Combine(
            Path.GetTempPath(),
            "arcanum-cancel-descendant-test-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(pidDirectory);

        string pidFile = Path.Combine(pidDirectory, "descendant.pid");

        try
        {
            ProcessStartInfo psi = new()
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"sleep 60 </dev/null >/dev/null 2>&1 & echo $! > '{pidFile}'; sleep 60");

            using CancellationTokenSource cancellation = new();

            cancellation.CancelAfter(TimeSpan.FromSeconds(2));

            // No post-start setpgid can ever succeed (the child has already exec'd), so the runner has no
            // process group of its own to kill on macOS. What kills the forked sleep on cancellation is the
            // tree kill — SIGKILL to every descendant still attached to the root, which never lets the
            // launcher's EXIT trap run — proven here with the supervisor taken out of play. The run passes
            // on the code that still had the dead setpgid fallback too: it guards the behaviour, and
            // UnixProcessGroupTests.No_production_code_calls_setpgid guards the fallback.
            CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.SpellScript,
                totalOutputCapBytes: 65_536,
                timeout: Timeout.InfiniteTimeSpan,
                resourceLimits: null,
                resourceLimiter: null,
                cancellation.Token,
                descendantSupervisorFactory: static (_, _) => null);

            Assert.Equal(CappedChildProcessOutcome.Canceled, result.Outcome);

            int descendantPid = int.Parse(
                (await File.ReadAllTextAsync(pidFile)).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(
                await WaitForProcessExitOrZombieAsync(
                    descendantPid,
                    TimeSpan.FromSeconds(5)),
                $"Forked descendant {descendantPid} survived cancellation.");
        }
        finally
        {
            TestDescendantProcess.KillRecorded(pidFile);

            Directory.Delete(pidDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_revalidates_trusted_identity_immediately_before_spawn()
    {
        ProcessStartInfo psi = CreateHarmlessEchoProcessStartInfo();
        int validationCount = 0;

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                psi,
                ChildProcessEnvironmentProfile.WorkspaceCheck,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(30),
                resourceLimits: null,
                resourceLimiter: null,
                CancellationToken.None,
                preStartValidation: () =>
                {
                    validationCount++;
                    return new CappedChildProcessPreStartValidationResult(
                        false,
                        "trusted identity changed");
                });

        Assert.Equal(1, validationCount);
        Assert.Equal(
            CappedChildProcessOutcome.PreStartValidationFailed,
            result.Outcome);
        Assert.Empty(result.Stdout.Text ?? string.Empty);
    }

    [Fact]
    public async Task RunAsync_rechecks_outer_cancellation_after_prestart_validation()
    {
        ProcessStartInfo startInfo = CreateHarmlessEchoProcessStartInfo();
        using CancellationTokenSource cancellation = new();

        CappedChildProcessRunResult result =
            await CappedChildProcessRunner.RunAsync(
                startInfo,
                ChildProcessEnvironmentProfile.WorkspaceCheck,
                totalOutputCapBytes: 65_536,
                timeout: TimeSpan.FromSeconds(30),
                resourceLimits: null,
                resourceLimiter: null,
                cancellation.Token,
                preStartValidation: () =>
                {
                    cancellation.Cancel();
                    return new CappedChildProcessPreStartValidationResult(
                        true,
                        Error: null,
                        Code: null);
                });

        Assert.Equal(
            CappedChildProcessOutcome.CanceledBeforeStart,
            result.Outcome);
        Assert.Empty(result.Stdout.Text ?? string.Empty);
    }

    [Fact]
    public void ApplyProfile_ToolExec_strips_arcanum_prefixed_keys_and_keeps_others()
    {
        ProcessStartInfo psi = new()
        {
            FileName = "noop",
        };

        psi.Environment["ARCANUM_Arcanum__Providers__0__ApiKey"] = "sk-secret";

        psi.Environment["arcanum_lower"] = "also-secret";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(psi, ChildProcessEnvironmentProfile.ToolExec);

        Assert.False(psi.Environment.ContainsKey("ARCANUM_Arcanum__Providers__0__ApiKey"));

        Assert.False(psi.Environment.ContainsKey("arcanum_lower"));

        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);
    }

    [Fact]
    public void ApplyProfile_ToolExec_strips_hijackable_variables_but_preserves_path()
    {
        ProcessStartInfo psi = new()
        {
            FileName = "noop",
        };

        // Interpreter/dynamic-linker preload hooks, credential-phishing SSH/Git helpers, TLS key
        // logging, and proxy redirection — the same denylist MCP child processes are scrubbed
        // against by default (McpSecurityLimits.IsBlockedEnvironmentVariable).
        psi.Environment["LD_PRELOAD"] = "/tmp/evil.so";

        psi.Environment["NODE_OPTIONS"] = "--require /tmp/evil.js";

        psi.Environment["PYTHONPATH"] = "/tmp/evil-site-packages";

        psi.Environment["GIT_SSH_COMMAND"] = "/tmp/steal-creds.sh";

        psi.Environment["SSLKEYLOGFILE"] = "/tmp/keys.log";

        psi.Environment["HTTPS_PROXY"] = "http://attacker.example/";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(psi, ChildProcessEnvironmentProfile.ToolExec);

        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));

        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));

        Assert.False(psi.Environment.ContainsKey("PYTHONPATH"));

        Assert.False(psi.Environment.ContainsKey("GIT_SSH_COMMAND"));

        Assert.False(psi.Environment.ContainsKey("SSLKEYLOGFILE"));

        Assert.False(psi.Environment.ContainsKey("HTTPS_PROXY"));

        // PATH is deliberately preserved: execute_command's entire purpose is running arbitrary
        // shell commands that need normal PATH resolution to work at all.
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);
    }

    [Fact]
    public void ApplyProfile_SpellScript_strips_arcanum_and_hijack_vars_like_ToolExec()
    {
        ProcessStartInfo psi = new()
        {
            FileName = "noop",
        };

        psi.Environment["ARCANUM_Arcanum__Providers__0__ApiKey"] = "sk-secret";

        psi.Environment["LD_PRELOAD"] = "/tmp/evil.so";

        psi.Environment["DOTNET_STARTUP_HOOKS"] = "/tmp/hook.dll";

        psi.Environment["NODE_OPTIONS"] = "--require /tmp/evil.js";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(psi, ChildProcessEnvironmentProfile.SpellScript);

        Assert.False(psi.Environment.ContainsKey("ARCANUM_Arcanum__Providers__0__ApiKey"));

        Assert.False(psi.Environment.ContainsKey("LD_PRELOAD"));

        Assert.False(psi.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));

        Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));

        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);
    }

    [Theory]
    [InlineData(ChildProcessEnvironmentProfile.ToolExec)]
    [InlineData(ChildProcessEnvironmentProfile.SpellScript)]
    public void ApplyProfile_strips_operator_declared_secret_variables_by_name(
        ChildProcessEnvironmentProfile profile)
    {
        // Arcanum's own secrets need not carry the ARCANUM_ prefix. The operator can point
        // Arcanum:Providers:*:CredentialEnvironmentVariable — and the HTTPS certificate password,
        // the Comm Link webhook URL, the web-research key, the A2A outbound credential — at any
        // portable name, and ConfigurationValidator blesses it. A variable called MY_OPENAI_KEY is
        // no less Arcanum's secret for being named that (see FamiliarSecretEnvironmentNames), so
        // the prefix scrub is the backstop and the operator-declared names are the control.
        ProcessStartInfo psi = new()
        {
            FileName = "noop",
        };

        psi.Environment["MY_OPENAI_KEY"] = "sk-secret";

        psi.Environment["TEAM_WEBHOOK_URL"] = "https://hooks.example/T/B/secret";

        psi.Environment["PATH"] = "/usr/bin";

        psi.Environment["HOME"] = "/home/user";

        ChildProcessEnvironmentScrubber.ApplyProfile(
            psi,
            profile,
            ["MY_OPENAI_KEY", "team_webhook_url", "  ", null!]);

        Assert.False(psi.Environment.ContainsKey("MY_OPENAI_KEY"));

        // Matched case-insensitively: the configured spelling need not match the exported one, and
        // over-removing a name the operator called a secret is the safe direction.
        Assert.False(psi.Environment.ContainsKey("TEAM_WEBHOOK_URL"));

        Assert.Equal("/usr/bin", psi.Environment["PATH"]);

        Assert.Equal("/home/user", psi.Environment["HOME"]);
    }

    [Fact]
    public async Task RunAsync_strips_operator_declared_secret_variables_from_the_child()
    {
        ProcessStartInfo psi = CreateEnvironmentEchoProcessStartInfo("MY_OPENAI_KEY");

        psi.Environment["MY_OPENAI_KEY"] = "sk-secret";

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.ToolExec,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(15),
            resourceLimits: null,
            resourceLimiter: null,
            CancellationToken.None,
            operatorDeclaredSecretEnvironmentVariables: ["MY_OPENAI_KEY"]);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.DoesNotContain(
            "sk-secret",
            result.Stdout.Text,
            StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task RunAsync_SigKillExit_NotClassifiedAsMemory_WhenNoOomEvidence()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "No POSIX signal-exit-code semantics on Windows; nothing to verify on this host.");

        ProcessStartInfo psi = CreateSelfSigKillProcessStartInfo();

        ResourceLimits limits = new() { MaxMemoryMb = 256 };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: limits,
            resourceLimiter: new FakeResourceLimiter(wasOomKilled: false),
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.Completed, result.Outcome);

        Assert.Null(result.ExceededResource);
    }

    [SkippableFact]
    public async Task RunAsync_SigKillExit_ClassifiedAsMemory_WhenOomEvidenceConfirmed()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "No POSIX signal-exit-code semantics on Windows; nothing to verify on this host.");

        ProcessStartInfo psi = CreateSelfSigKillProcessStartInfo();

        ResourceLimits limits = new() { MaxMemoryMb = 256 };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: limits,
            resourceLimiter: new FakeResourceLimiter(wasOomKilled: true),
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitExceeded, result.Outcome);

        Assert.Equal(ResourceLimitKind.Memory, result.ExceededResource);
    }

    private static ProcessStartInfo CreateSelfSigKillProcessStartInfo() =>
        new()
        {
            FileName = "/bin/sh",

            ArgumentList = { "-c", "kill -9 $$" },

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,
        };

    /// <summary>
    /// Bypasses real OS-level enforcement entirely (Apply leaves ProcessStartInfo untouched) so
    /// tests can isolate CappedChildProcessRunner's exit-code classification logic from the actual
    /// setrlimit/cgroups mechanics covered by ProcessResourceLimiterTests.
    /// </summary>
    private sealed class FakeResourceLimiter(bool? wasOomKilled) : IProcessResourceLimiter
    {
        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) =>
            new(null, null, wasOomKilled is null ? null : () => Task.FromResult(wasOomKilled.Value));
    }

    /// <summary>Leaves the start info untouched and hands the runner a cleanup callback to invoke.</summary>
    private sealed class CleanupRecordingLimiter(Func<Task> cleanup) : IProcessResourceLimiter
    {
        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) =>
            new(null, _ => cleanup());
    }

    /// <summary>
    /// Reports a ceiling the runner itself has to enforce (what the macOS limiter does), without
    /// rewriting the start info.
    /// </summary>
    private sealed class MonitoredMemoryLimiter(long monitoredLimitBytes) : IProcessResourceLimiter
    {
        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) =>
            new(null, null, MonitoredMemoryLimitBytes: monitoredLimitBytes);
    }

    private static ProcessStartInfo CreateHarmlessEchoProcessStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList = { "-NoProfile", "-Command", $"Write-Output {SentinelToken}" },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/echo",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { SentinelToken },
        };
    }

    private static ProcessStartInfo CreateEnvironmentEchoProcessStartInfo(string variableName)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    $"Write-Output $env:{variableName}",
                },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { "-c", $"printf '%s' \"${variableName}\"" },
        };
    }

    /// <summary>
    /// Drains fd 0 to completion and only then prints the sentinel, so the child finishes promptly
    /// exactly when the runner handed it a closed pipe and blocks when it inherited a console.
    /// </summary>
    private static ProcessStartInfo CreateStdinReadingProcessStartInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    $"$null = [Console]::In.ReadToEnd(); Write-Output {SentinelToken}",
                },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { "-c", $"cat > /dev/null; echo {SentinelToken}" },
        };
    }

    private static ProcessStartInfo CreateLargeOutputProcessStartInfo(int payloadCharCount)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    $"Write-Output ('x' * {payloadCharCount})",
                },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { "-c", $"printf '%*s' {payloadCharCount} | tr ' ' 'x'" },
        };
    }

    private static ProcessStartInfo CreateLargeOutputThenSleepProcessStartInfo(
        int payloadCharCount,
        int seconds)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList =
                {
                    "-NoProfile",
                    "-Command",
                    $"[Console]::Out.Write(('x' * {payloadCharCount})); Start-Sleep -Seconds {seconds}",
                },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sh",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList =
            {
                "-c",
                $"printf '%*s' {payloadCharCount} | tr ' ' 'x'; sleep {seconds}",
            },
        };
    }

    private static async Task<bool> WaitForProcessExitOrZombieAsync(
        int processId,
        TimeSpan timeout)
    {
        long deadline = global::System.Environment.TickCount64
            + (long)Math.Max(1, timeout.TotalMilliseconds);

        do
        {
            if (IsExitedOrZombie(processId))
            {
                return true;
            }

            await Task.Delay(25);
        }

        while (global::System.Environment.TickCount64 < deadline);

        return IsExitedOrZombie(processId);
    }

    private static bool IsExitedOrZombie(int processId)
    {
        if (OperatingSystem.IsLinux())
        {
            string statPath = $"/proc/{processId}/stat";
            if (File.Exists(statPath))
            {
                try
                {
                    string stat = File.ReadAllText(statPath);
                    int commandEnd = stat.LastIndexOf(')');
                    if (commandEnd >= 0
                        && commandEnd + 2 < stat.Length
                        && stat[commandEnd + 2] is 'Z' or 'X')
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        try
        {
            using global::System.Diagnostics.Process process =
                global::System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static ProcessStartInfo CreateSleepProcessStartInfo(int seconds)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ProcessStartInfo
            {
                FileName = "powershell.exe",

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

                ArgumentList = { "-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}" },
            };
        }

        return new ProcessStartInfo
        {
            FileName = "/bin/sleep",

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,

            ArgumentList = { seconds.ToString() },
        };
    }
}
