using System.Diagnostics;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;

namespace RetroDownfall.Arcanum.Tests.Platform;

[Collection("ChildProcess")]
public sealed class ProcessResourceLimiterTests
{
    private readonly ProcessResourceLimiter _limiter = new();

    [Fact]
    public void Apply_returns_null_for_unlimited_resources()
    {
        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("hi");

        ResourceLimits limits = new()
        {
            MaxProcessMemoryMb = 0,
            MaxProcessCount = 0,
            MaxCpuSeconds = 0,
            MaxMemoryMb = 0,
            MaxFileDescriptors = 0,
        };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        Assert.Null(result.CleanupAsync);

        // No limits configured: StartInfo must be left completely untouched (no shell prelude).
        Assert.Equal("/bin/echo", psi.FileName);

        Assert.Equal(["hi"], psi.ArgumentList);
    }

    [Fact]
    public void Apply_returns_error_when_filename_missing()
    {
        ProcessStartInfo psi = new() { FileName = string.Empty };

        ResourceLimits limits = new() { MaxCpuSeconds = 30 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.NotNull(result.Error);

        Assert.Null(result.CleanupAsync);
    }

    [SkippableFact]
    public async Task Apply_returns_assign_after_start_on_windows()
    {
        Skip.If(
            !OperatingSystem.IsWindows(),
            "Windows Job Object path; nothing to verify on this host.");

        ProcessStartInfo psi = new() { FileName = "cmd.exe" };

        ResourceLimits limits = new() { MaxCpuSeconds = 30, MaxMemoryMb = 512, MaxFileDescriptors = 256 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        Assert.NotNull(result.AssignAfterStart);

        Assert.NotNull(result.CleanupAsync);

        // Job Objects do not rewrite StartInfo (unlike the Unix ulimit prelude).
        Assert.Equal("cmd.exe", psi.FileName);

        // Dispose the job without starting a child (empty job + KILL_ON_JOB_CLOSE is fine).
        await result.CleanupAsync!(0);
    }

    [SkippableFact]
    public void Apply_rewrites_startinfo_with_ulimit_prelude_on_unix()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only behavior (setrlimit via the ulimit shell prelude); nothing to verify here.");

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("hello world");

        psi.ArgumentList.Add("--flag");

        ResourceLimits limits = new() { MaxCpuSeconds = 30, MaxMemoryMb = 0, MaxProcessMemoryMb = 0, MaxFileDescriptors = 256 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        Assert.Equal("/bin/sh", psi.FileName);

        Assert.Equal("-c", psi.ArgumentList[0]);

        string script = psi.ArgumentList[1];

        Assert.Contains("ulimit -t 30 || exit 126; ", script, StringComparison.Ordinal);

        Assert.Contains("ulimit -n 256 || exit 126; ", script, StringComparison.Ordinal);

        // Both memory ceilings were 0 (unlimited): no -v clause should be emitted.
        Assert.DoesNotContain("ulimit -v", script, StringComparison.Ordinal);

        Assert.Equal("sh", psi.ArgumentList[2]);

        // Original argv arrives intact and positionally — never string-interpolated — so an
        // argument containing spaces/quotes is passed through unmodified as a single argv entry.
        Assert.Equal("/bin/echo", psi.ArgumentList[3]);

        Assert.Equal("hello world", psi.ArgumentList[4]);

        Assert.Equal("--flag", psi.ArgumentList[5]);
    }

    /// <summary>
    /// <c>ulimit -v</c> (RLIMIT_AS) takes kilobytes while the effective memory ceiling is megabytes.
    /// The clause is a real ceiling only where the kernel accepts it: Linux falls back to it when no
    /// cgroups v2 scope could be created. macOS rejects RLIMIT_AS every time (<c>cannot modify limit:
    /// Invalid argument</c>), so the old claim that this clause was "the shipping memory-enforcement
    /// path" on macOS was false — the shell printed the error and still exec'd the target with no
    /// ceiling. The macOS prelude must never emit it (the fail-closed prelude would otherwise refuse
    /// every child); the macOS ceiling is the runner's physical-footprint monitor, handed over as
    /// <see cref="ProcessResourceLimiterResult.MonitoredMemoryLimitBytes"/>.
    /// </summary>
    [SkippableFact]
    public void Apply_emits_the_memory_limit_in_kilobytes_where_the_kernel_accepts_it()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only behavior (setrlimit via the ulimit shell prelude); nothing to verify here.");

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        ResourceLimits limits = new() { MaxCpuSeconds = 30, MaxMemoryMb = 512, MaxFileDescriptors = 0 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        string script = psi.ArgumentList[1];

        if (OperatingSystem.IsMacOS())
        {
            Assert.DoesNotContain("ulimit -v", script, StringComparison.Ordinal);

            Assert.Equal(512L * 1024L * 1024L, result.MonitoredMemoryLimitBytes);
        }
        else
        {
            // Linux prefers a cgroups v2 scope for memory and only falls back to the prelude when the
            // scope could not be created.
            Assert.True(
                script.Contains("ulimit -v 524288 || exit 126; ", StringComparison.Ordinal)
                || script.Contains("cgroup.procs", StringComparison.Ordinal),
                $"Expected either the kilobyte ulimit clause or a cgroups v2 scope, got: {script}");
        }
    }

    /// <summary>
    /// R-234: <see cref="ResourceLimits.MaxProcessMemoryMb"/> (always clamped to at least 64) and
    /// <see cref="ResourceLimits.MaxMemoryMb"/> resolve to one effective ceiling on every platform —
    /// the smaller of the configured non-zero values — instead of the per-process value counting
    /// only on Windows.
    /// </summary>
    [Theory]
    [InlineData(0, 128, 128)]
    [InlineData(128, 0, 128)]
    [InlineData(1024, 256, 256)]
    [InlineData(256, 1024, 256)]
    [InlineData(512, 512, 512)]
    [InlineData(0, 0, 0)]
    public void EffectiveMemoryLimit_UsesMinOfNonZeroValues_OnAllPlatforms(
        int maxMemoryMb,
        int maxProcessMemoryMb,
        int expected)
    {
        ResourceLimits limits = new() { MaxMemoryMb = maxMemoryMb, MaxProcessMemoryMb = maxProcessMemoryMb };

        Assert.Equal(expected, ProcessResourceLimiter.EffectiveMemoryLimitMb(limits));
    }

    /// <summary>
    /// R-234: a per-process ceiling alone (<c>MaxMemoryMb = 0</c>) still bounds a Unix child.
    /// </summary>
    [SkippableFact]
    public void Apply_enforces_the_per_process_memory_ceiling_on_unix_when_MaxMemoryMb_is_zero()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only behavior; the Windows Job Object path is pinned by WindowsJobObjectSessionTests.");

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        ResourceLimits limits = new()
        {
            MaxCpuSeconds = 30,
            MaxMemoryMb = 0,
            MaxProcessMemoryMb = 128,
            MaxFileDescriptors = 0,
            MaxProcessCount = 0,
        };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(128L * 1024L * 1024L, result.MonitoredMemoryLimitBytes);
        }
        else
        {
            string script = psi.ArgumentList[1];

            Assert.True(
                script.Contains("ulimit -v 131072 || exit 126; ", StringComparison.Ordinal)
                || script.Contains("cgroup.procs", StringComparison.Ordinal),
                $"Expected the per-process ceiling to be enforced, got: {script}");
        }
    }

    /// <summary>
    /// R-006: a <c>ulimit</c> the shell cannot apply prints an error and returns non-zero; joined
    /// with <c>; </c> the prelude still <c>exec</c>ed the target with no limit. Every clause must exit
    /// 126 before <c>exec</c> instead, and that refusal must leave a per-run marker on stderr so the
    /// runner can tell it from a target's own exit 126. Proven on the real shell by lowering the hard
    /// descriptor limit below the configured one before the prelude runs.
    /// </summary>
    [SkippableFact]
    public async Task Prelude_FailsClosed_WhenUlimitRejected()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only behavior (setrlimit via the ulimit shell prelude); nothing to verify here.");

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("target-ran");

        ResourceLimits limits = new() { MaxCpuSeconds = 30, MaxMemoryMb = 512, MaxFileDescriptors = 256 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        Assert.Equal("/bin/sh", psi.FileName);

        string script = psi.ArgumentList[1];

        MatchCollection clauses = Regex.Matches(script, "ulimit -[a-z] [0-9]+");

        Assert.NotEmpty(clauses);

        foreach (Match clause in clauses)
        {
            Assert.True(
                script.AsSpan(clause.Index + clause.Length).StartsWith(" || exit 126; ", StringComparison.Ordinal),
                $"Clause '{clause.Value}' is not followed by a fail-closed exit: {script}");
        }

        // The Linux cgroup-join clause is not checked here: Apply emits it only on a host where a cgroups v2
        // scope was created, so a check against this script would assert nothing on every other host.
        // Prelude_FailsClosed_WhenCgroupJoinRejected builds that clause directly and runs it on any Unix.
        Assert.EndsWith("exec \"$@\"", script, StringComparison.Ordinal);

        Assert.False(string.IsNullOrEmpty(result.PreExecFailureMarker));

        // Real shell: a hard descriptor limit of 64 makes the prelude's `ulimit -n 256` fail.
        using System.Diagnostics.Process process = new();

        process.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        process.StartInfo.ArgumentList.Add("-c");

        process.StartInfo.ArgumentList.Add("ulimit -n 64 || exit 3; exec \"$@\"");

        process.StartInfo.ArgumentList.Add("sh");

        process.StartInfo.ArgumentList.Add(psi.FileName);

        foreach (string argument in psi.ArgumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start());

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        string stdout = await stdoutTask;

        string stderr = await stderrTask;

        Assert.Equal(126, process.ExitCode);

        Assert.DoesNotContain("target-ran", stdout, StringComparison.Ordinal);

        Assert.Contains(result.PreExecFailureMarker!, stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Linux cgroup-join step is a prelude clause like any <c>ulimit</c>: a refused join (the scope
    /// directory is gone, or delegation was revoked) must exit 126 before <c>exec</c> and leave the
    /// per-run marker, never run the target outside its memory scope. The clause is built with a scope
    /// path that cannot exist, so a plain shell proves the refusal on any Unix host.
    /// </summary>
    [SkippableFact]
    public async Task Prelude_FailsClosed_WhenCgroupJoinRejected()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only behavior (the shell prelude); nothing to verify here.");

        const string marker = "arcanum-resource-limit-not-applied-test";

        string missingScope = Path.Combine(Path.GetTempPath(), "arcanum-missing-scope-" + Guid.NewGuid().ToString("N"));

        string script = ProcessResourceLimiter.BuildUlimitPrelude(
            new ResourceLimits { MaxFileDescriptors = 256 },
            includeMemory: false,
            cgroupPath: missingScope,
            marker)!;

        // Shape: the join precedes every ulimit and is followed by the same fail-closed exit.
        Match join = Regex.Match(script, "echo \\$\\$ > \"[^\"]+/cgroup\\.procs\"");

        Assert.True(join.Success, script);

        Assert.True(
            script.AsSpan(join.Index + join.Length).StartsWith(" || exit 126; ", StringComparison.Ordinal),
            $"The cgroup join is not followed by a fail-closed exit: {script}");

        // Behavior: a real shell cannot write into a scope directory that does not exist.
        using System.Diagnostics.Process process = new();

        process.StartInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-c", script, "sh", "/bin/echo", "target-ran" },
        };

        Assert.True(process.Start());

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(126, process.ExitCode);

        Assert.DoesNotContain("target-ran", await stdoutTask, StringComparison.Ordinal);

        Assert.Contains(marker, await stderrTask, StringComparison.Ordinal);
    }

    /// <summary>
    /// The success path must neither emit the marker nor alter the target's own output or status.
    /// </summary>
    [SkippableFact]
    public async Task Prelude_runs_the_target_without_the_marker_when_every_limit_applies()
    {
        Skip.If(
            OperatingSystem.IsWindows(),
            "Unix-only behavior (setrlimit via the ulimit shell prelude); nothing to verify here.");

        ProcessStartInfo psi = new()
        {
            FileName = "/bin/echo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        psi.ArgumentList.Add("target-ran");

        ResourceLimits limits = new() { MaxCpuSeconds = 30, MaxMemoryMb = 512, MaxFileDescriptors = 256 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        Assert.False(string.IsNullOrEmpty(result.PreExecFailureMarker));

        using System.Diagnostics.Process process = new() { StartInfo = psi };

        Assert.True(process.Start());

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();

        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, process.ExitCode);

        Assert.Contains("target-ran", await stdoutTask, StringComparison.Ordinal);

        Assert.DoesNotContain(result.PreExecFailureMarker!, await stderrTask, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Apply_writes_cgroup_files_on_linux()
    {
        Skip.If(
            !OperatingSystem.IsLinux(),
            "cgroups v2 is Linux-only; nothing to verify on this host.");

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("hi");

        ResourceLimits limits = new() { MaxCpuSeconds = 10, MaxMemoryMb = 256, MaxFileDescriptors = 100 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        if (result.CleanupAsync is null)
        {
            // /sys/fs/cgroup is not writable in this sandbox (no cgroup delegation): the limiter
            // has already fallen back to the setrlimit-only ulimit prelude, which is verified by
            // Apply_rewrites_startinfo_with_ulimit_prelude_on_unix. Nothing further to assert here.
            return;
        }

        string script = psi.ArgumentList[1];

        Match match = Regex.Match(script, "echo \\$\\$ > \"([^\"]+)/cgroup\\.procs\"");

        Assert.True(match.Success, $"Expected a cgroup join line in the prelude script: {script}");

        string cgroupPath = match.Groups[1].Value;

        try
        {
            Assert.True(Directory.Exists(cgroupPath));

            string memoryMax = await File.ReadAllTextAsync(Path.Combine(cgroupPath, "memory.max"));

            Assert.Equal((256L * 1024L * 1024L).ToString(), memoryMax.Trim());

            string memoryHigh = await File.ReadAllTextAsync(Path.Combine(cgroupPath, "memory.high"));

            Assert.Equal((256L * 1024L * 1024L).ToString(), memoryHigh.Trim());

            Assert.True(File.Exists(Path.Combine(cgroupPath, "cpu.max")));
        }
        finally
        {
            await result.CleanupAsync(0);
        }

        Assert.False(Directory.Exists(cgroupPath));
    }

    [SkippableFact]
    public async Task Apply_ExposesWasOomKilledAsync_WhenCgroupIsAvailable()
    {
        Skip.If(
            !OperatingSystem.IsLinux(),
            "cgroups v2 is Linux-only; nothing to verify on this host.");

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("hi");

        ResourceLimits limits = new() { MaxMemoryMb = 256 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        if (result.CleanupAsync is null)
        {
            // No cgroup delegation available in this sandbox: the limiter already fell back to
            // setrlimit-only enforcement, which exposes no OOM evidence — nothing further to assert.
            return;
        }

        try
        {
            // Exposed whenever a real cgroups v2 scope backs this invocation (see ApplyOnLinux),
            // so CappedChildProcessRunner can require authoritative OOM evidence before attributing
            // a SIGKILL/SIGSEGV exit to the configured memory limit.
            Assert.NotNull(result.WasOomKilledAsync);

            // A freshly created, never-scheduled cgroup has not triggered any kernel OOM kill yet.
            Assert.False(await result.WasOomKilledAsync!());
        }
        finally
        {
            await result.CleanupAsync(0);
        }
    }
}
