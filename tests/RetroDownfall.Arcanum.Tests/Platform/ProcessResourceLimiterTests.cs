using System.Diagnostics;
using System.Text.RegularExpressions;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;

namespace RetroDownfall.Arcanum.Tests.Platform;

public sealed class ProcessResourceLimiterTests
{

    private readonly ProcessResourceLimiter _limiter = new();

    [Fact]
    public void Apply_returns_null_for_unlimited_resources()
    {

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("hi");

        ResourceLimits limits = new() { MaxCpuSeconds = 0, MaxMemoryMb = 0, MaxFileDescriptors = 0 };

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

    [Fact]
    public async Task Apply_returns_assign_after_start_on_windows()
    {

        if (!OperatingSystem.IsWindows())
        {

            // Windows Job Object path; nothing to verify on this host.
            return;

        }

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

    [Fact]
    public void Apply_rewrites_startinfo_with_ulimit_prelude_on_unix()
    {

        if (OperatingSystem.IsWindows())
        {

            // Unix-only behavior (setrlimit via the ulimit shell prelude); nothing to verify here.
            return;

        }

        ProcessStartInfo psi = new() { FileName = "/bin/echo" };

        psi.ArgumentList.Add("hello world");

        psi.ArgumentList.Add("--flag");

        ResourceLimits limits = new() { MaxCpuSeconds = 30, MaxMemoryMb = 0, MaxFileDescriptors = 256 };

        ProcessResourceLimiterResult result = _limiter.Apply(psi, limits);

        Assert.Null(result.Error);

        Assert.Equal("/bin/sh", psi.FileName);

        Assert.Equal("-c", psi.ArgumentList[0]);

        string script = psi.ArgumentList[1];

        Assert.Contains("ulimit -t 30", script, StringComparison.Ordinal);

        Assert.Contains("ulimit -n 256", script, StringComparison.Ordinal);

        // MaxMemoryMb was 0 (unlimited): no -v clause should be emitted.
        Assert.DoesNotContain("ulimit -v", script, StringComparison.Ordinal);

        Assert.Equal("sh", psi.ArgumentList[2]);

        // Original argv arrives intact and positionally — never string-interpolated — so an
        // argument containing spaces/quotes is passed through unmodified as a single argv entry.
        Assert.Equal("/bin/echo", psi.ArgumentList[3]);

        Assert.Equal("hello world", psi.ArgumentList[4]);

        Assert.Equal("--flag", psi.ArgumentList[5]);

    }

    [Fact]
    public async Task Apply_writes_cgroup_files_on_linux()
    {

        if (!OperatingSystem.IsLinux())
        {

            // cgroups v2 is Linux-only; nothing to verify on this host.
            return;

        }

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

    [Fact]
    public async Task Apply_ExposesWasOomKilledAsync_WhenCgroupIsAvailable()
    {

        if (!OperatingSystem.IsLinux())
        {

            // cgroups v2 is Linux-only; nothing to verify on this host.
            return;

        }

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
