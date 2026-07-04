using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Infrastructure.Platform;

/// <summary>
/// Cross-platform OS-level enforcement of <see cref="ResourceLimits"/> for child processes.
/// </summary>
/// <remarks>
/// <para>
/// .NET exposes no fork/pre-exec hook, so P/Invoking <c>setrlimit</c> in the parent process before
/// <see cref="Process.Start()"/> is unsafe: e.g. lowering <c>RLIMIT_AS</c> on the host itself can make
/// the subsequent <c>Start()</c> call fail. Instead, on macOS and as a Linux fallback, this limiter
/// rewrites <see cref="ProcessStartInfo.FileName"/>/<see cref="ProcessStartInfo.ArgumentList"/> to route
/// the invocation through a <c>/bin/sh -c '...; exec "$@"'</c> prelude that calls the <c>ulimit</c>
/// shell builtin (which itself calls <c>setrlimit</c>) inside the child, before it execs the real
/// target. Every original argument is passed as a separate <c>argv</c> entry so the shell sees them
/// positionally (<c>$1..$N</c>) with no word-splitting, globbing, or injection risk.
/// </para>
/// <para>
/// On Linux, cgroups v2 is preferred for memory enforcement because it triggers an accurate RSS-based
/// OOM kill (<c>memory.max</c>/<c>memory.high</c>), whereas <c>RLIMIT_AS</c> only bounds virtual
/// address space (a process can reserve far more address space than it ever touches, so RLIMIT_AS is
/// a weaker proxy for physical memory pressure). CPU time and file descriptors are still enforced via
/// the <c>ulimit</c> prelude on Linux too, since cgroups v2 has no file-descriptor controller and its
/// <c>cpu.max</c> only rate-throttles (caps to one core) rather than killing once a cumulative CPU-time
/// budget is exceeded — only <c>RLIMIT_CPU</c> delivers that kill semantics (SIGXCPU) on both platforms.
/// </para>
/// <para>
/// Known gap: cgroups v2 cgroup membership covers the entire process subtree (grandchildren included),
/// but the <c>ulimit</c>/setrlimit path only bounds the direct child — a grandchild process spawned by
/// the tool script is not rlimit-bound by this mechanism. This is an accepted limitation of setrlimit,
/// not a bug.
/// </para>
/// </remarks>
public sealed class ProcessResourceLimiter(ILogger<ProcessResourceLimiter>? logger = null) : IProcessResourceLimiter
{

    private const string CgroupRoot = "/sys/fs/cgroup";

    private static int _windowsWarningLogged;

    public ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits)
    {

        ArgumentNullException.ThrowIfNull(startInfo);

        ArgumentNullException.ThrowIfNull(limits);

        if (!HasAnyLimit(limits))
        {

            return new ProcessResourceLimiterResult(null, null);

        }

        if (string.IsNullOrEmpty(startInfo.FileName))
        {

            return new ProcessResourceLimiterResult(
                new ResourceLimitError("execute_command: no target executable was specified for resource-limited execution."),
                null);

        }

        if (OperatingSystem.IsWindows())
        {

            LogWindowsWarningOnce();

            return new ProcessResourceLimiterResult(null, null);

        }

        if (OperatingSystem.IsLinux())
        {

            return ApplyOnLinux(startInfo, limits);

        }

        if (OperatingSystem.IsMacOS())
        {

            return ApplyOnMacOs(startInfo, limits);

        }

        // Unsupported/unknown OS: fail open with no enforcement, same as Windows, rather than
        // blocking every tool invocation on a platform we have not validated setrlimit/cgroups on.
        return new ProcessResourceLimiterResult(null, null);

    }

    private static bool HasAnyLimit(ResourceLimits limits) =>
        limits.MaxCpuSeconds > 0 || limits.MaxMemoryMb > 0 || limits.MaxFileDescriptors > 0;

    private void LogWindowsWarningOnce()
    {

        if (Interlocked.Exchange(ref _windowsWarningLogged, 1) != 0)
        {

            return;

        }

        logger?.LogWarning(
            "Sanctum resource limits (CPU time, memory, file descriptors) are not enforced on Windows; "
            + "only path, network, and tool restrictions apply to execute_command/run_spell_script.");

    }

    private static ProcessResourceLimiterResult ApplyOnMacOs(ProcessStartInfo startInfo, ResourceLimits limits)
    {

        string prelude = BuildUlimitPrelude(limits, includeMemory: true);

        RewriteToShellPrelude(startInfo, prelude);

        return new ProcessResourceLimiterResult(null, null);

    }

    private ProcessResourceLimiterResult ApplyOnLinux(ProcessStartInfo startInfo, ResourceLimits limits)
    {

        string? cgroupPath = TryCreateAndConfigureCgroup(limits);

        // Memory is handled by cgroups (accurate RSS-based OOM) when available; CPU time and file
        // descriptors always go through the ulimit prelude since cgroups v2 cannot enforce either.
        string prelude = BuildUlimitPrelude(limits, includeMemory: cgroupPath is null);

        if (cgroupPath is not null)
        {

            // The child (the shell itself, before it execs the real target) joins the cgroup by
            // writing its own pid. cgroups v2 migration is by-process and exec() preserves the pid,
            // so the eventual target process ends up in the cgroup too — the .NET side never needs
            // to learn the OS pid before Process.Start() returns it.
            prelude = $"echo $$ > \"{cgroupPath}/cgroup.procs\" 2>/dev/null; " + prelude;

        }

        RewriteToShellPrelude(startInfo, prelude);

        Func<int, Task>? cleanup = cgroupPath is null
            ? null
            : _ => DeleteCgroupAsync(cgroupPath);

        return new ProcessResourceLimiterResult(null, cleanup);

    }

    private string? TryCreateAndConfigureCgroup(ResourceLimits limits)
    {

        if (limits.MaxMemoryMb <= 0)
        {

            // Nothing for cgroups to contribute; CPU/FD are always handled by the ulimit prelude.
            return null;

        }

        // A GUID-named scope (rather than a pid-named one) sidesteps the pid-reuse race entirely,
        // since Apply() runs before Process.Start() and the child pid is not yet known.
        string path = Path.Combine(CgroupRoot, $"arcanum-{Guid.NewGuid():N}.scope");

        try
        {

            Directory.CreateDirectory(path);

        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {

            // /sys/fs/cgroup not mounted, or cgroup delegation not available to this user: fall back
            // to setrlimit (via the ulimit prelude) for memory too.
            logger?.LogDebug(ex, "Sanctum could not create a cgroups v2 scope; falling back to setrlimit.");

            return null;

        }

        try
        {

            WriteCgroupLimits(path, limits);

            return path;

        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {

            logger?.LogDebug(ex, "Sanctum could not configure a cgroups v2 scope; falling back to setrlimit.");

            TryDeleteCgroupDirectory(path);

            return null;

        }

    }

    private static void WriteCgroupLimits(string cgroupPath, ResourceLimits limits)
    {

        long memoryBytes = (long)limits.MaxMemoryMb * 1024L * 1024L;

        string memoryText = memoryBytes.ToString(CultureInfo.InvariantCulture);

        File.WriteAllText(Path.Combine(cgroupPath, "memory.max"), memoryText);

        File.WriteAllText(Path.Combine(cgroupPath, "memory.high"), memoryText);

        if (limits.MaxCpuSeconds > 0)
        {

            // cpu.max is "<quota> <period>" in microseconds; the kernel clamps period to at most
            // 1_000_000us (1s). quota == period therefore caps the process to at most one full CPU
            // core rather than expressing a cumulative CPU-time budget — this is a defense-in-depth
            // rate throttle only. The authoritative CPU-time cutoff (which kills the process with
            // SIGXCPU once MaxCpuSeconds of CPU time have actually been consumed) comes from
            // RLIMIT_CPU, applied via the ulimit prelude regardless of cgroup availability.
            File.WriteAllText(Path.Combine(cgroupPath, "cpu.max"), "1000000 1000000");

        }

    }

    private Task DeleteCgroupAsync(string cgroupPath)
    {

        TryDeleteCgroupDirectory(cgroupPath);

        return Task.CompletedTask;

    }

    private void TryDeleteCgroupDirectory(string cgroupPath)
    {

        try
        {

            Directory.Delete(cgroupPath, recursive: false);

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {

            // Best-effort cleanup: a leaked, empty, process-less cgroup directory is cosmetic, not a
            // security concern. Nothing further to do here.
            logger?.LogDebug(ex, "Failed to delete a transient Sanctum cgroups v2 scope directory.");

        }

    }

    private static string BuildUlimitPrelude(ResourceLimits limits, bool includeMemory)
    {

        StringBuilder script = new();

        if (limits.MaxCpuSeconds > 0)
        {

            script.Append("ulimit -t ").Append(limits.MaxCpuSeconds).Append("; ");

        }

        if (includeMemory && limits.MaxMemoryMb > 0)
        {

            long memoryKb = (long)limits.MaxMemoryMb * 1024L;

            script.Append("ulimit -v ").Append(memoryKb.ToString(CultureInfo.InvariantCulture)).Append("; ");

        }

        if (limits.MaxFileDescriptors > 0)
        {

            script.Append("ulimit -n ").Append(limits.MaxFileDescriptors).Append("; ");

        }

        script.Append("exec \"$@\"");

        return script.ToString();

    }

    /// <summary>
    /// Rewrites <paramref name="startInfo"/> to launch the original target through a POSIX shell
    /// prelude. Every original argv entry (file name plus each existing item in
    /// <see cref="ProcessStartInfo.ArgumentList"/>) is passed as its own argument to <c>sh</c>, so the
    /// script only ever references them via <c>$1</c>/<c>"$@"</c> — never string-interpolated — which
    /// means arguments containing spaces, quotes, or <c>$</c> pass through unmodified with no shell
    /// word-splitting, globbing, or injection risk.
    /// </summary>
    private static void RewriteToShellPrelude(ProcessStartInfo startInfo, string preludeScript)
    {

        string targetFileName = startInfo.FileName;

        List<string> originalArguments = [..startInfo.ArgumentList];

        startInfo.ArgumentList.Clear();

        startInfo.ArgumentList.Add("-c");

        startInfo.ArgumentList.Add(preludeScript);

        // $0 for the inner shell (conventionally "sh"); the real target becomes $1.
        startInfo.ArgumentList.Add("sh");

        startInfo.ArgumentList.Add(targetFileName);

        foreach (string argument in originalArguments)
        {

            startInfo.ArgumentList.Add(argument);

        }

        startInfo.FileName = "/bin/sh";

    }

}
