using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Infrastructure.Platform;

/// <summary>
/// Narrow abstraction over Windows Job Object kernel calls so unit tests can exercise create /
/// limit / assign / close sequencing with fakes without requiring a Windows host.
/// </summary>
internal interface IWindowsJobObjectApi
{
    SafeJobHandle? CreateJobObject();

    bool ConfigureLimits(SafeJobHandle job, in WindowsJobObjectLimits limits);

    bool AssignProcess(SafeJobHandle job, SafeHandle processHandle);

    int GetLastError();
}

/// <summary>
/// Limit values applied via <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c>.
/// </summary>
internal readonly record struct WindowsJobObjectLimits(
    ulong? ProcessMemoryBytes,
    ulong? JobMemoryBytes,
    long? PerProcessUserTime100Ns,
    uint? ActiveProcessLimit);

/// <summary>
/// Owns a Windows Job Object handle. Disposing the job with
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> terminates any remaining processes in the job.
/// </summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeJobHandle(nint preexistingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return WindowsJobObjectInterop.CloseHandle(handle);
    }
}

/// <summary>
/// AOT-safe (<c>LibraryImport</c>) P/Invoke surface for Windows Job Objects used by
/// <see cref="ProcessResourceLimiter"/> to enforce Sanctum resource limits on child processes.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: Windows-only kernel interop; behavior covered via IWindowsJobObjectApi fakes + OS-conditional integration tests.
internal static partial class WindowsJobObjectInterop
{
    internal const int JobObjectInfoClassExtendedLimitInformation = 9;

    internal const uint JobObjectLimitProcessTime = 0x0000_0002;

    internal const uint JobObjectLimitActiveProcess = 0x0000_0008;

    internal const uint JobObjectLimitProcessMemory = 0x0000_0100;

    internal const uint JobObjectLimitJobMemory = 0x0000_0200;

    internal const uint JobObjectLimitKillOnJobClose = 0x0000_2000;

    /// <summary>NTSTATUS <c>STATUS_QUOTA_EXCEEDED</c> — common exit code when a job memory limit kills a process.</summary>
    internal const int StatusQuotaExceeded = unchecked((int)0xC0000044);

    internal static IWindowsJobObjectApi CreateDefaultApi()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Job Objects require Windows.");
        }

        return new NativeWindowsJobObjectApi();
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint hJob,
        int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInformation,
        uint cbJobObjectInformationLength);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;

        public long PerJobUserTimeLimit;

        public uint LimitFlags;

        public nuint MinimumWorkingSetSize;

        public nuint MaximumWorkingSetSize;

        public uint ActiveProcessLimit;

        public nuint Affinity;

        public uint PriorityClass;

        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;

        public ulong WriteOperationCount;

        public ulong OtherOperationCount;

        public ulong ReadTransferCount;

        public ulong WriteTransferCount;

        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;

        public IoCounters IoInfo;

        public nuint ProcessMemoryLimit;

        public nuint JobMemoryLimit;

        public nuint PeakProcessMemoryUsed;

        public nuint PeakJobMemoryUsed;
    }

    [SupportedOSPlatform("windows")]
    private sealed class NativeWindowsJobObjectApi : IWindowsJobObjectApi
    {
        public SafeJobHandle? CreateJobObject()
        {
            nint handle = CreateJobObjectW(0, null);

            if (handle == 0)
            {
                return null;
            }

            return new SafeJobHandle(handle, ownsHandle: true);
        }

        public bool ConfigureLimits(SafeJobHandle job, in WindowsJobObjectLimits limits)
        {
            JobObjectExtendedLimitInformation info = default;

            uint flags = JobObjectLimitKillOnJobClose;

            if (limits.PerProcessUserTime100Ns is long cpuTicks and > 0)
            {
                flags |= JobObjectLimitProcessTime;

                info.BasicLimitInformation.PerProcessUserTimeLimit = cpuTicks;
            }

            if (limits.ActiveProcessLimit is uint active and > 0)
            {
                flags |= JobObjectLimitActiveProcess;

                info.BasicLimitInformation.ActiveProcessLimit = active;
            }

            if (limits.ProcessMemoryBytes is ulong processMemory and > 0)
            {
                flags |= JobObjectLimitProcessMemory;

                info.ProcessMemoryLimit = (nuint)processMemory;
            }

            if (limits.JobMemoryBytes is ulong jobMemory and > 0)
            {
                flags |= JobObjectLimitJobMemory;

                info.JobMemoryLimit = (nuint)jobMemory;
            }

            info.BasicLimitInformation.LimitFlags = flags;

            return SetInformationJobObject(
                job.DangerousGetHandle(),
                JobObjectInfoClassExtendedLimitInformation,
                ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>());
        }

        public bool AssignProcess(SafeJobHandle job, SafeHandle processHandle) =>
            AssignProcessToJobObject(job.DangerousGetHandle(), processHandle.DangerousGetHandle());

        public int GetLastError() => Marshal.GetLastPInvokeError();
    }
}

/// <summary>
/// Creates and configures a Windows Job Object for one child-process invocation, then assigns
/// the live process after <see cref="Process.Start()"/>.
/// </summary>
internal sealed class WindowsJobObjectSession : IDisposable
{
    /// <summary>
    /// Committed memory the job-wide ceiling leaves for the trusted sandbox broker, which is a member of
    /// the job beside the tool it creates. Sized for the Native AOT apphost the shipping broker is.
    /// </summary>
    internal const ulong BrokerJobMemoryHeadroomBytes = 64UL * 1024UL * 1024UL;

    private readonly IWindowsJobObjectApi _api;

    private readonly ResourceLimits _limits;

    private SafeJobHandle? _job;

    private WindowsJobObjectSession(IWindowsJobObjectApi api, ResourceLimits limits, SafeJobHandle job)
    {
        _api = api;

        _limits = limits;

        _job = job;
    }

    /// <summary>
    /// Builds the Win32 limit payload from <see cref="ResourceLimits"/>. Returns <c>null</c> when
    /// no Job Object–enforceable limit is configured (caller should skip job creation).
    /// </summary>
    /// <param name="limits">The configured Sanctum limits.</param>
    /// <param name="brokered">
    /// <see langword="true"/> when the process assigned to the job is the trusted sandbox broker rather
    /// than the tool. The broker creates the tool inside the same job and stays alive until it exits, so
    /// it takes one active-process slot of its own: the limit is the configured count plus that slot, and
    /// the job-wide memory ceiling gains <see cref="BrokerJobMemoryHeadroomBytes"/>. The per-process
    /// memory ceiling and the CPU limit are unchanged, because they bind the tool alone. The tool's tree
    /// therefore gets exactly <see cref="ResourceLimits.MaxProcessCount"/> active processes, as it does
    /// when the tool itself is assigned.
    /// </param>
    internal static WindowsJobObjectLimits? BuildLimits(ResourceLimits limits, bool brokered = false)
    {
        ulong? memoryBytes = ResolveMemoryBytes(limits);

        long? cpuTicks = limits.MaxCpuSeconds > 0
            ? (long)limits.MaxCpuSeconds * 10_000_000L
            : null;

        uint? activeProcesses = limits.MaxProcessCount > 0
            ? (uint)limits.MaxProcessCount
            : null;

        if (memoryBytes is null && cpuTicks is null && activeProcesses is null)
        {
            return null;
        }

        ulong? jobMemoryBytes = memoryBytes;

        if (brokered)
        {
            activeProcesses += 1;

            jobMemoryBytes += BrokerJobMemoryHeadroomBytes;
        }

        return new WindowsJobObjectLimits(
            ProcessMemoryBytes: memoryBytes,
            JobMemoryBytes: jobMemoryBytes,
            PerProcessUserTime100Ns: cpuTicks,
            ActiveProcessLimit: activeProcesses);
    }

    internal static bool HasJobEnforceableLimits(ResourceLimits limits) =>
        BuildLimits(limits) is not null;

    internal static WindowsJobObjectSession? TryCreate(
        ResourceLimits limits,
        IWindowsJobObjectApi api,
        out ResourceLimitError? error)
    {
        error = null;

        WindowsJobObjectLimits? built = BuildLimits(limits);

        if (built is null)
        {
            return null;
        }

        SafeJobHandle? job = api.CreateJobObject();

        if (job is null || job.IsInvalid)
        {
            job?.Dispose();

            error = new ResourceLimitError(
                "execute_command: Windows Job Object could not be created for resource-limited execution.");

            return null;
        }

        if (!api.ConfigureLimits(job, built.Value))
        {
            int lastError = api.GetLastError();

            job.Dispose();

            error = new ResourceLimitError(
                $"execute_command: Windows Job Object limits could not be configured (Win32 error {lastError}).");

            return null;
        }

        return new WindowsJobObjectSession(api, limits, job);
    }

    /// <summary>
    /// Makes room in the job for the sandbox broker, which is about to be assigned to it. Called once,
    /// before <see cref="Assign"/>, and only for a run whose started process is the broker: the limits
    /// are written again as <see cref="BuildLimits"/> builds them for a brokered job, so the tool the
    /// broker creates keeps the configured process count.
    /// </summary>
    internal ResourceLimitError? ReserveBrokerSlot()
    {
        SafeJobHandle? job = _job;

        if (job is null || job.IsInvalid)
        {
            return new ResourceLimitError(
                "execute_command: Windows Job Object handle was already closed before the sandbox broker was accounted for.");
        }

        WindowsJobObjectLimits? brokered = BuildLimits(_limits, brokered: true);

        if (brokered is null)
        {
            return null;
        }

        if (!_api.ConfigureLimits(job, brokered.Value))
        {
            int lastError = _api.GetLastError();

            return new ResourceLimitError(
                $"execute_command: Windows Job Object limits could not be widened for the sandbox broker (Win32 error {lastError}).");
        }

        return null;
    }

    internal ResourceLimitError? Assign(Process process)
    {
        SafeJobHandle? job = _job;

        if (job is null || job.IsInvalid)
        {
            return new ResourceLimitError(
                "execute_command: Windows Job Object handle was already closed before assignment.");
        }

        SafeHandle processHandle;

        try
        {
            processHandle = process.SafeHandle;
        }
        catch (InvalidOperationException)
        {
            return new ResourceLimitError(
                "execute_command: child process handle was unavailable for Job Object assignment.");
        }

        if (!_api.AssignProcess(job, processHandle))
        {
            int lastError = _api.GetLastError();

            // ERROR_ACCESS_DENIED (5) / ERROR_INVALID_PARAMETER (87): process already belongs to an
            // incompatible job (e.g. nested under a host Job Object that forbids reassignment).
            return new ResourceLimitError(
                $"execute_command: child process could not be assigned to the Sanctum Job Object (Win32 error {lastError}). "
                + "The process may already belong to an incompatible job.");
        }

        return null;
    }

    public void Dispose()
    {
        SafeJobHandle? job = Interlocked.Exchange(ref _job, null);

        job?.Dispose();
    }

    private static ulong? ResolveMemoryBytes(ResourceLimits limits)
    {
        int memoryMb = ProcessResourceLimiter.EffectiveMemoryLimitMb(limits);

        if (memoryMb <= 0)
        {
            return null;
        }

        return (ulong)memoryMb * 1024UL * 1024UL;
    }
}
