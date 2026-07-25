using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Infrastructure.Platform;

namespace RetroDownfall.Arcanum.Tests.Platform;

public sealed class ProcessResourceLimiterWindowsBehaviorTests
{

    [Fact]
    public void Apply_rejects_null_arguments_before_platform_dispatch()
    {

        ProcessResourceLimiter limiter = new();

        Assert.Throws<ArgumentNullException>(() =>
            limiter.Apply(null!, new ResourceLimits()));

        Assert.Throws<ArgumentNullException>(() =>
            limiter.Apply(new ProcessStartInfo { FileName = "ignored" }, null!));

    }

    [SkippableFact]
    public void Apply_on_windows_skips_job_for_file_descriptor_limit_only()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object behavior requires Windows.");

        FakeWindowsJobObjectApi api = new();

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessStartInfo startInfo = new() { FileName = "cmd.exe" };

        ProcessResourceLimiterResult result = limiter.Apply(
            startInfo,
            new ResourceLimits
            {
                MaxProcessMemoryMb = 0,
                MaxProcessCount = 0,
                MaxCpuSeconds = 0,
                MaxMemoryMb = 0,
                MaxFileDescriptors = 32,
            });

        Assert.Null(result.Error);

        Assert.Null(result.AssignAfterStart);

        Assert.Null(result.CleanupAsync);

        Assert.Equal(0, api.CreateCount);

        Assert.Equal("cmd.exe", startInfo.FileName);

    }

    [SkippableFact]
    public void Apply_on_windows_rejects_missing_executable_before_job_creation()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object behavior requires Windows.");

        FakeWindowsJobObjectApi api = new();

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessResourceLimiterResult result = limiter.Apply(
            new ProcessStartInfo { FileName = string.Empty },
            new ResourceLimits { MaxMemoryMb = 64 });

        Assert.NotNull(result.Error);

        Assert.Equal(
            "execute_command: no target executable was specified for resource-limited execution.",
            result.Error.Message);

        Assert.Null(result.AssignAfterStart);

        Assert.Null(result.CleanupAsync);

        Assert.Equal(0, api.CreateCount);

    }

    [SkippableFact]
    public void Apply_on_windows_surfaces_job_creation_failure()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object behavior requires Windows.");

        FakeWindowsJobObjectApi api = new() { JobCreation = CreateResult.Null };

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessResourceLimiterResult result = limiter.Apply(
            new ProcessStartInfo { FileName = "cmd.exe" },
            new ResourceLimits { MaxMemoryMb = 64 });

        Assert.NotNull(result.Error);

        Assert.Contains("could not be created", result.Error.Message, StringComparison.Ordinal);

        Assert.Null(result.AssignAfterStart);

        Assert.Null(result.CleanupAsync);

        Assert.Equal(1, api.CreateCount);

        Assert.Equal(0, api.ConfigureCount);

    }

    [SkippableFact]
    public void Apply_on_windows_treats_invalid_job_handle_as_creation_failure()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object behavior requires Windows.");

        FakeWindowsJobObjectApi api = new() { JobCreation = CreateResult.Invalid };

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessResourceLimiterResult result = limiter.Apply(
            new ProcessStartInfo { FileName = "cmd.exe" },
            new ResourceLimits { MaxProcessCount = 1 });

        Assert.NotNull(result.Error);

        Assert.Contains("could not be created", result.Error.Message, StringComparison.Ordinal);

        Assert.NotNull(api.LastCreatedHandle);

        Assert.True(api.LastCreatedHandle.IsClosed);

        Assert.Equal(0, api.ConfigureCount);

    }

    [SkippableFact]
    public void Apply_on_windows_surfaces_limit_configuration_failure()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object behavior requires Windows.");

        FakeWindowsJobObjectApi api = new()
        {
            ConfigureResult = false,
            LastError = 87,
        };

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessResourceLimiterResult result = limiter.Apply(
            new ProcessStartInfo { FileName = "cmd.exe" },
            new ResourceLimits { MaxCpuSeconds = 1 });

        Assert.NotNull(result.Error);

        Assert.Contains("could not be configured", result.Error.Message, StringComparison.Ordinal);

        Assert.Contains("87", result.Error.Message, StringComparison.Ordinal);

        Assert.Null(result.AssignAfterStart);

        Assert.Null(result.CleanupAsync);

        Assert.NotNull(api.LastCreatedHandle);

        Assert.True(api.LastCreatedHandle.IsClosed);

    }

    [SkippableFact]
    public async Task Apply_on_windows_assigns_through_fake_and_cleanup_closes_session()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows Job Object behavior requires Windows.");

        FakeWindowsJobObjectApi api = new();

        ProcessResourceLimiter limiter = new(logger: null, windowsJobApi: api);

        ProcessResourceLimiterResult result = limiter.Apply(
            new ProcessStartInfo { FileName = "cmd.exe" },
            new ResourceLimits
            {
                MaxProcessMemoryMb = 32,
                MaxMemoryMb = 64,
                MaxProcessCount = 2,
            });

        Assert.Null(result.Error);

        Assert.NotNull(result.AssignAfterStart);

        Assert.NotNull(result.CleanupAsync);

        using global::System.Diagnostics.Process current =
            global::System.Diagnostics.Process.GetCurrentProcess();

        ResourceLimitError? assignError = result.AssignAfterStart!(current);

        Assert.Null(assignError);

        Assert.Equal(1, api.AssignCount);

        Assert.NotNull(api.LastAssignedProcessHandle);

        await result.CleanupAsync!(current.Id);

        Assert.NotNull(api.LastCreatedHandle);

        Assert.True(api.LastCreatedHandle.IsClosed);

    }

    [Fact]
    public void SafeJobHandle_default_constructor_creates_invalid_owned_handle()
    {

        SafeJobHandle handle = new();

        Assert.True(handle.IsInvalid);

        Assert.False(handle.IsClosed);

        handle.Dispose();

        Assert.True(handle.IsClosed);

    }

    [SkippableFact]
    public void SafeJobHandle_on_windows_closes_transferred_kernel_handle()
    {

        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Windows kernel-handle ownership requires Windows.");

        using EventWaitHandle waitHandle = new(
            initialState: false,
            EventResetMode.AutoReset);

        SafeWaitHandle transferred = waitHandle.SafeWaitHandle;

        nint rawHandle = transferred.DangerousGetHandle();

        transferred.SetHandleAsInvalid();

        SafeJobHandle jobHandle = new(rawHandle, ownsHandle: true);

        Assert.False(jobHandle.IsInvalid);

        jobHandle.Dispose();

        Assert.True(jobHandle.IsClosed);

    }

    private enum CreateResult
    {

        Valid,

        Null,

        Invalid,

    }

    private sealed class FakeWindowsJobObjectApi : IWindowsJobObjectApi
    {

        public CreateResult JobCreation { get; init; }

        public bool ConfigureResult { get; init; } = true;

        public int LastError { get; init; } = 5;

        public int CreateCount { get; private set; }

        public int ConfigureCount { get; private set; }

        public int AssignCount { get; private set; }

        public SafeJobHandle? LastCreatedHandle { get; private set; }

        public SafeHandle? LastAssignedProcessHandle { get; private set; }

        public SafeJobHandle? CreateJobObject()
        {

            CreateCount++;

            LastCreatedHandle = JobCreation switch
            {
                CreateResult.Null => null,
                CreateResult.Invalid => new SafeJobHandle(nint.Zero, ownsHandle: false),
                _ => new SafeJobHandle(new nint(1), ownsHandle: false),
            };

            return LastCreatedHandle;

        }

        public bool ConfigureLimits(SafeJobHandle job, in WindowsJobObjectLimits limits)
        {

            ConfigureCount++;

            return ConfigureResult;

        }

        public bool AssignProcess(SafeJobHandle job, SafeHandle processHandle)
        {

            AssignCount++;

            LastAssignedProcessHandle = processHandle;

            return true;

        }

        public int GetLastError() => LastError;

    }

}
