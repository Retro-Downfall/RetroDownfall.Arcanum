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

    /// <summary>
    /// The trusted broker is the process assigned to the job and it creates the tool inside the same
    /// job, so a limit of exactly <c>MaxProcessCount</c> would leave the tool no slot at all when the
    /// count is 1. An unbrokered job, where the tool itself is assigned, keeps exactly the count.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void BuildLimits_gives_a_brokered_job_one_more_slot_than_an_unbrokered_one(int maxProcessCount)
    {
        ResourceLimits limits = new() { MaxProcessCount = maxProcessCount };

        WindowsJobObjectLimits? unbrokered = WindowsJobObjectSession.BuildLimits(limits);

        WindowsJobObjectLimits? brokered = WindowsJobObjectSession.BuildLimits(limits, brokered: true);

        Assert.Equal((uint)maxProcessCount, unbrokered!.Value.ActiveProcessLimit);

        Assert.Equal((uint)maxProcessCount + 1, brokered!.Value.ActiveProcessLimit);
    }

    [Fact]
    public void BuildLimits_brokered_leaves_the_per_process_ceiling_and_cpu_limit_alone()
    {
        ResourceLimits limits = new() { MaxCpuSeconds = 12, MaxMemoryMb = 64, MaxProcessMemoryMb = 128 };

        WindowsJobObjectLimits unbrokered = WindowsJobObjectSession.BuildLimits(limits)!.Value;

        WindowsJobObjectLimits brokered = WindowsJobObjectSession.BuildLimits(limits, brokered: true)!.Value;

        Assert.Equal(unbrokered.ProcessMemoryBytes, brokered.ProcessMemoryBytes);

        Assert.Equal(unbrokered.PerProcessUserTime100Ns, brokered.PerProcessUserTime100Ns);

        Assert.Equal(64UL * 1024UL * 1024UL, unbrokered.JobMemoryBytes);

        Assert.Equal(
            64UL * 1024UL * 1024UL + WindowsJobObjectSession.BrokerJobMemoryHeadroomBytes,
            brokered.JobMemoryBytes);
    }

    [Fact]
    public void BuildLimits_brokered_does_not_invent_a_limit_the_operator_did_not_set()
    {
        ResourceLimits limits = new()
        {
            MaxCpuSeconds = 5,
            MaxMemoryMb = 0,
            MaxProcessMemoryMb = 0,
            MaxProcessCount = 0,
        };

        WindowsJobObjectLimits brokered = WindowsJobObjectSession.BuildLimits(limits, brokered: true)!.Value;

        Assert.Null(brokered.ActiveProcessLimit);

        Assert.Null(brokered.ProcessMemoryBytes);

        Assert.Null(brokered.JobMemoryBytes);
    }

    [Fact]
    public void BuildLimits_brokered_still_returns_null_when_nothing_is_enforceable()
    {
        ResourceLimits limits = new()
        {
            MaxCpuSeconds = 0,
            MaxMemoryMb = 0,
            MaxProcessMemoryMb = 0,
            MaxProcessCount = 0,
        };

        Assert.Null(WindowsJobObjectSession.BuildLimits(limits, brokered: true));
    }

    [Fact]
    public void ReserveBrokerSlot_rewrites_the_limits_with_the_broker_slot_and_nothing_else_changes()
    {
        FakeWindowsJobObjectApi api = new();

        ResourceLimits limits = new() { MaxCpuSeconds = 5, MaxMemoryMb = 32, MaxProcessCount = 1 };

        using WindowsJobObjectSession? session = WindowsJobObjectSession.TryCreate(limits, api, out _);

        Assert.NotNull(session);

        Assert.Equal(1u, api.LastLimits!.Value.ActiveProcessLimit);

        Assert.Null(session.ReserveBrokerSlot());

        Assert.Equal(2, api.ConfigureCount);

        Assert.Equal(2u, api.LastLimits.Value.ActiveProcessLimit);

        Assert.Equal(32UL * 1024UL * 1024UL, api.LastLimits.Value.ProcessMemoryBytes);

        Assert.Equal(5L * 10_000_000L, api.LastLimits.Value.PerProcessUserTime100Ns);

        Assert.Equal(0, api.AssignCount);
    }

    [Fact]
    public void ReserveBrokerSlot_reports_a_limit_write_that_the_kernel_refused()
    {
        FakeWindowsJobObjectApi api = new();

        using WindowsJobObjectSession? session = WindowsJobObjectSession.TryCreate(
            new ResourceLimits { MaxProcessCount = 1 },
            api,
            out _);

        Assert.NotNull(session);

        api.FailConfigure = true;

        api.LastError = 87;

        ResourceLimitError? error = session.ReserveBrokerSlot();

        Assert.NotNull(error);

        Assert.Contains("broker", error.Message, StringComparison.Ordinal);

        Assert.Contains("87", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReserveBrokerSlot_after_dispose_reports_the_closed_handle()
    {
        FakeWindowsJobObjectApi api = new();

        WindowsJobObjectSession? session = WindowsJobObjectSession.TryCreate(
            new ResourceLimits { MaxProcessCount = 1 },
            api,
            out _);

        Assert.NotNull(session);

        session.Dispose();

        ResourceLimitError? error = session.ReserveBrokerSlot();

        Assert.NotNull(error);

        Assert.Contains("already closed", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runner binds the started process to the job. A started broker widens the job first and only
    /// then is assigned; the tool assigned directly is bound as it is, with no reservation.
    /// </summary>
    [Fact]
    public void BindToJobObject_reserves_the_broker_slot_before_assigning_a_started_broker()
    {
        List<string> calls = [];

        ProcessResourceLimiterResult limiterResult = RecordingLimiterResult(calls, reserveError: null, assignError: null);

        ChildProcessSandboxApplyResult brokered = new()
        {
            Status = ChildProcessSandboxApplyStatus.Applied,

            WindowsJobAssignedSignalPath = "broker.signal",
        };

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.GetCurrentProcess();

        Assert.Null(CappedChildProcessRunner.BindToJobObject(limiterResult, brokered, process));

        Assert.Equal(["reserve", "assign"], calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindToJobObject_assigns_a_started_tool_without_a_reservation(bool hasSandboxResult)
    {
        List<string> calls = [];

        ProcessResourceLimiterResult limiterResult = RecordingLimiterResult(calls, reserveError: null, assignError: null);

        ChildProcessSandboxApplyResult? unbrokered = hasSandboxResult
            ? new ChildProcessSandboxApplyResult { Status = ChildProcessSandboxApplyStatus.EscapedByOperator }
            : null;

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.GetCurrentProcess();

        Assert.Null(CappedChildProcessRunner.BindToJobObject(limiterResult, unbrokered, process));

        Assert.Equal(["assign"], calls);
    }

    [Fact]
    public void BindToJobObject_does_not_assign_a_broker_whose_slot_could_not_be_reserved()
    {
        List<string> calls = [];

        ResourceLimitError refused = new("reserve refused");

        ProcessResourceLimiterResult limiterResult = RecordingLimiterResult(calls, reserveError: refused, assignError: null);

        ChildProcessSandboxApplyResult brokered = new()
        {
            Status = ChildProcessSandboxApplyStatus.Applied,

            WindowsJobAssignedSignalPath = "broker.signal",
        };

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.GetCurrentProcess();

        Assert.Same(refused, CappedChildProcessRunner.BindToJobObject(limiterResult, brokered, process));

        Assert.Equal(["reserve"], calls);
    }

    [Fact]
    public void BindToJobObject_reports_the_assignment_failure_of_a_broker()
    {
        List<string> calls = [];

        ResourceLimitError failed = new("assign failed");

        ProcessResourceLimiterResult limiterResult = RecordingLimiterResult(calls, reserveError: null, assignError: failed);

        ChildProcessSandboxApplyResult brokered = new()
        {
            Status = ChildProcessSandboxApplyStatus.Applied,

            WindowsJobAssignedSignalPath = "broker.signal",
        };

        using global::System.Diagnostics.Process process = global::System.Diagnostics.Process.GetCurrentProcess();

        Assert.Same(failed, CappedChildProcessRunner.BindToJobObject(limiterResult, brokered, process));

        Assert.Equal(["reserve", "assign"], calls);
    }

    private static ProcessResourceLimiterResult RecordingLimiterResult(
        List<string> calls,
        ResourceLimitError? reserveError,
        ResourceLimitError? assignError) =>
        new(
            null,
            CleanupAsync: _ => Task.CompletedTask,
            AssignAfterStart: _ =>
            {
                calls.Add("assign");

                return assignError;
            },
            ReserveBrokerSlot: () =>
            {
                calls.Add("reserve");

                return reserveError;
            });

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

    /// <summary>
    /// R-234: the Job Object enforces one effective ceiling — the smaller non-zero of
    /// <see cref="ResourceLimits.MaxMemoryMb"/> and <see cref="ResourceLimits.MaxProcessMemoryMb"/> — so a
    /// quota kill must be attributed to memory under that same ceiling. Classifying on
    /// <see cref="ResourceLimits.MaxMemoryMb"/> alone reported a kill under the per-process ceiling as an
    /// ordinary exit. Pure, so it runs on every host; the real Windows exit path is pinned below.
    /// </summary>
    [Theory]
    [InlineData(0, 128, true)]
    [InlineData(64, 0, true)]
    [InlineData(64, 128, true)]
    [InlineData(0, 0, false)]
    public void Job_quota_exit_is_attributed_to_memory_under_the_effective_ceiling(
        int maxMemoryMb,
        int maxProcessMemoryMb,
        bool attributedToMemory)
    {
        ResourceLimits limits = new() { MaxMemoryMb = maxMemoryMb, MaxProcessMemoryMb = maxProcessMemoryMb };

        Assert.Equal(
            attributedToMemory ? (ResourceLimitKind?)ResourceLimitKind.Memory : null,
            CappedChildProcessRunner.ClassifyWindowsJobExit(WindowsJobObjectInterop.StatusQuotaExceeded, limits));

        Assert.Null(CappedChildProcessRunner.ClassifyWindowsJobExit(1, limits));
    }

    /// <summary>
    /// Windows lane: a real child exiting with <c>STATUS_QUOTA_EXCEEDED</c> while only the per-process
    /// ceiling is configured is reported as a memory breach, not as a completed run. Not run on macOS.
    /// </summary>
    [SkippableFact]
    public async Task CappedChildProcessRunner_quota_exit_under_only_the_per_process_ceiling_is_a_memory_breach_on_windows()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Job Object exit classification is Windows behavior.");

        ProcessStartInfo psi = new()
        {
            FileName = "cmd.exe",

            ArgumentList = { "/c", "exit", "-1073741756" },

            RedirectStandardOutput = true,

            RedirectStandardError = true,

            UseShellExecute = false,

            CreateNoWindow = true,
        };

        CappedChildProcessRunResult result = await CappedChildProcessRunner.RunAsync(
            psi,
            ChildProcessEnvironmentProfile.SpellScript,
            totalOutputCapBytes: 65_536,
            timeout: TimeSpan.FromSeconds(30),
            resourceLimits: new ResourceLimits { MaxMemoryMb = 0, MaxProcessMemoryMb = 128 },
            resourceLimiter: null,
            CancellationToken.None);

        Assert.Equal(WindowsJobObjectInterop.StatusQuotaExceeded, result.ExitCode);

        Assert.Equal(CappedChildProcessOutcome.ResourceLimitExceeded, result.Outcome);

        Assert.Equal(ResourceLimitKind.Memory, result.ExceededResource);
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
