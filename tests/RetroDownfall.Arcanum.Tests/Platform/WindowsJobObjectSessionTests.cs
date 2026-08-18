using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Platform;

[Collection("ChildProcess")]
public sealed class WindowsJobObjectSessionTests
{

    [Fact]
    public void BuildLimits_returns_null_when_no_job_enforceable_fields()
    {

        ResourceLimits limits = new()
        {
            MaxCpuSeconds = 0,
            MaxMemoryMb = 0,
            MaxProcessMemoryMb = 0,
            MaxProcessCount = 0,
            MaxFileDescriptors = 256,
        };

        Assert.Null(WindowsJobObjectSession.BuildLimits(limits));

        Assert.False(WindowsJobObjectSession.HasJobEnforceableLimits(limits));

    }

    [Fact]
    public void BuildLimits_maps_memory_cpu_and_active_process()
    {

        ResourceLimits limits = new()
        {
            MaxCpuSeconds = 12,
            MaxMemoryMb = 64,
            MaxProcessMemoryMb = 128,
            MaxProcessCount = 3,
        };

        WindowsJobObjectLimits? built = WindowsJobObjectSession.BuildLimits(limits);

        Assert.NotNull(built);

        // Tighter of MaxMemoryMb / MaxProcessMemoryMb.
        Assert.Equal(64UL * 1024UL * 1024UL, built.Value.ProcessMemoryBytes);

        Assert.Equal(64UL * 1024UL * 1024UL, built.Value.JobMemoryBytes);

        Assert.Equal(12L * 10_000_000L, built.Value.PerProcessUserTime100Ns);

        Assert.Equal(3u, built.Value.ActiveProcessLimit);

    }

    [Fact]
    public void TryCreate_configures_kill_on_close_and_limits_via_api()
    {

        FakeWindowsJobObjectApi api = new();

        ResourceLimits limits = new() { MaxCpuSeconds = 5, MaxMemoryMb = 32, MaxProcessCount = 2 };

        using WindowsJobObjectSession? session = WindowsJobObjectSession.TryCreate(limits, api, out ResourceLimitError? error);

        Assert.Null(error);

        Assert.NotNull(session);

        Assert.Equal(1, api.CreateCount);

        Assert.Equal(1, api.ConfigureCount);

        Assert.NotNull(api.LastLimits);

        Assert.Equal(32UL * 1024UL * 1024UL, api.LastLimits!.Value.ProcessMemoryBytes);

        Assert.Equal(5L * 10_000_000L, api.LastLimits.Value.PerProcessUserTime100Ns);

        Assert.Equal(2u, api.LastLimits.Value.ActiveProcessLimit);

    }

    [Fact]
    public void TryCreate_returns_error_when_create_fails()
    {

        FakeWindowsJobObjectApi api = new() { FailCreate = true };

        WindowsJobObjectSession? session = WindowsJobObjectSession.TryCreate(
            new ResourceLimits { MaxMemoryMb = 64 },
            api,
            out ResourceLimitError? error);

        Assert.Null(session);

        Assert.NotNull(error);

        Assert.Contains("could not be created", error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void TryCreate_returns_error_when_configure_fails()
    {

        FakeWindowsJobObjectApi api = new() { FailConfigure = true, LastError = 87 };

        WindowsJobObjectSession? session = WindowsJobObjectSession.TryCreate(
            new ResourceLimits { MaxMemoryMb = 64 },
            api,
            out ResourceLimitError? error);

        Assert.Null(session);

        Assert.NotNull(error);

        Assert.Contains("could not be configured", error.Message, StringComparison.Ordinal);

        Assert.Contains("87", error.Message, StringComparison.Ordinal);

    }

    [Fact]
    public void Assign_returns_error_when_api_rejects()
    {

        FakeWindowsJobObjectApi api = new() { FailAssign = true, LastError = 5 };

        using WindowsJobObjectSession session = WindowsJobObjectSession.TryCreate(
            new ResourceLimits { MaxMemoryMb = 64 },
            api,
            out ResourceLimitError? createError)!;

        Assert.Null(createError);

        // Use current process handle as a stand-in — Assign only forwards the SafeHandle to the fake.
        using global::System.Diagnostics.Process self = global::System.Diagnostics.Process.GetCurrentProcess();

        ResourceLimitError? assignError = session.Assign(self);

        Assert.NotNull(assignError);

        Assert.Contains("could not be assigned", assignError.Message, StringComparison.Ordinal);

        Assert.Contains("5", assignError.Message, StringComparison.Ordinal);

        Assert.Equal(1, api.AssignCount);

    }

    [SkippableFact]
    public async Task ProcessResourceLimiter_Apply_uses_injected_windows_api_when_on_windows()
    {

        Skip.If(
            !OperatingSystem.IsWindows(),
            "Windows-only behaviour.");

        FakeWindowsJobObjectApi api = new();

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessStartInfo psi = new() { FileName = "cmd.exe" };

        ProcessResourceLimiterResult result = limiter.Apply(
            psi,
            new ResourceLimits { MaxCpuSeconds = 1, MaxMemoryMb = 64, MaxProcessCount = 4 });

        Assert.Null(result.Error);

        Assert.NotNull(result.AssignAfterStart);

        Assert.Equal(1, api.CreateCount);

        Assert.Equal(1, api.ConfigureCount);

        await result.CleanupAsync!(0);

    }

    [Fact]
    public async Task WindowsJobObject_AssignmentFailureKillsStartedProcess()
    {

        ProcessStartInfo psi;

        if (OperatingSystem.IsWindows())
        {

            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",

                ArgumentList = { "/c", "ping", "-n", "30", "127.0.0.1" },

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,
            };

        }
        else
        {

            psi = new ProcessStartInfo
            {
                FileName = "/bin/sleep",

                ArgumentList = { "30" },

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,
            };

        }

        AssignFailingLimiterWithCapture limiter = new();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 64 },
            resourceLimiter: limiter,
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitApplyFailed, result.Outcome);

        Assert.True(limiter.CapturedPid > 0);

        // Process object may be disposed by the runner; verify by pid that the tree was killed.
        // Poll briefly — ProcessTreeKiller is best-effort async w.r.t. kernel process table updates.
        bool stillAlive = true;

        for (int i = 0; i < 50 && stillAlive; i++)
        {
            try
            {
                using global::System.Diagnostics.Process surviving =
                    global::System.Diagnostics.Process.GetProcessById(limiter.CapturedPid);

                stillAlive = !surviving.HasExited;

                if (stillAlive)
                {
                    await Task.Delay(20);
                }
            }
            catch (ArgumentException)
            {
                stillAlive = false;
            }
        }

        Assert.False(
            stillAlive,
            "Assign-after-start failure must kill the started process tree before returning.");

    }

    [Fact]
    public async Task CappedChildProcessRunner_AssignAfterStartFailure_ReturnsApplyFailed()
    {

        ProcessStartInfo psi = CreateShortLivedProcessStartInfo();

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(10),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 64 },
            resourceLimiter: new AssignFailingLimiter(),
            CancellationToken.None);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitApplyFailed, result.Outcome);

        Assert.Contains("assign failed", result.ResourceLimitApplyError, StringComparison.OrdinalIgnoreCase);

    }

    private static ProcessStartInfo CreateShortLivedProcessStartInfo()
    {

        if (OperatingSystem.IsWindows())
        {

            return new ProcessStartInfo
            {

                FileName = "cmd.exe",

                ArgumentList = { "/c", "echo", "ok" },

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

            };

        }

        return new ProcessStartInfo
        {

            FileName = "/bin/echo",

            ArgumentList = { "ok" },

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

        };

    }

    private sealed class AssignFailingLimiter : IProcessResourceLimiter
    {

        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) =>
            new(
                null,
                CleanupAsync: _ => Task.CompletedTask,
                WasOomKilledAsync: null,
                AssignAfterStart: _ => new ResourceLimitError("Job Object assign failed (test)."));

    }

    private sealed class AssignFailingLimiterWithCapture : IProcessResourceLimiter
    {

        public int CapturedPid { get; private set; }

        public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits) =>
            new(
                null,
                CleanupAsync: _ => Task.CompletedTask,
                WasOomKilledAsync: null,
                AssignAfterStart: process =>
                {
                    CapturedPid = process.Id;

                    return new ResourceLimitError("Job Object assign failed (test).");
                });

    }

    private sealed class FakeWindowsJobObjectApi : IWindowsJobObjectApi
    {

        public bool FailCreate { get; set; }

        public bool FailConfigure { get; set; }

        public bool FailAssign { get; set; }

        public int LastError { get; set; } = 5;

        public int CreateCount { get; private set; }

        public int ConfigureCount { get; private set; }

        public int AssignCount { get; private set; }

        public WindowsJobObjectLimits? LastLimits { get; private set; }

        public SafeJobHandle? CreateJobObject()
        {

            CreateCount++;

            if (FailCreate)
            {

                return null;

            }

            // ownsHandle: false — never P/Invoke CloseHandle (safe on non-Windows test hosts).
            return new SafeJobHandle(new nint(1), ownsHandle: false);

        }

        public bool ConfigureLimits(SafeJobHandle job, in WindowsJobObjectLimits limits)
        {

            ConfigureCount++;

            LastLimits = limits;

            return !FailConfigure;

        }

        public bool AssignProcess(SafeJobHandle job, SafeHandle processHandle)
        {

            AssignCount++;

            return !FailAssign;

        }

        public int GetLastError() => LastError;

    }

}
