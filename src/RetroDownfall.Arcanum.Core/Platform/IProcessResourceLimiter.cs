using System.Diagnostics;
using RetroDownfall.Arcanum.Core.Sanctum;

namespace RetroDownfall.Arcanum.Core.Platform;

/// <summary>
/// Which OS-enforced resource limit was exceeded (or failed to apply), used both for the
/// sanitized model-facing denial message and for mapping a child process's signal-kill exit
/// code back to a specific limit.
/// </summary>
public enum ResourceLimitKind
{

    Cpu,

    Memory,

    FileDescriptors,

}

/// <summary>
/// Non-sensitive error returned when resource limits could not be applied to a
/// <see cref="ProcessStartInfo"/> before the child process starts, or could not be assigned
/// to the child after <see cref="Process.Start()"/> (Windows Job Objects). The message is safe
/// to log; it must never be surfaced verbatim to the model (callers wrap it in a generic denial).
/// </summary>
public sealed record ResourceLimitError(string Message);

/// <summary>
/// Result of attempting to apply <see cref="ResourceLimits"/> to a <see cref="ProcessStartInfo"/>.
/// </summary>
/// <param name="Error">Non-null when limits could not be applied; the process must not be started.</param>
/// <param name="CleanupAsync">
/// Non-null only when the limiter allocated OS-level state that must be torn down after the
/// child process exits (e.g. a transient Linux cgroups v2 directory, or a Windows Job Object
/// handle). The caller must invoke this with the started child's PID in a <c>finally</c> block
/// once the process has exited (or failed to start / failed post-start assign). Null on macOS
/// and the setrlimit/ulimit fallback paths, which have nothing to clean up.
/// </param>
/// <param name="WasOomKilledAsync">
/// Non-null only when the limiter enforces memory via a mechanism that records authoritative OOM
/// evidence (a Linux cgroups v2 scope's <c>memory.events</c> <c>oom_kill</c> counter). When present,
/// callers observing a SIGKILL/SIGSEGV exit code should use this to confirm the configured memory
/// limit was the actual cause before attributing the kill to it — a bare exit code alone cannot
/// distinguish a Sanctum-enforced OOM kill from an unrelated external <c>kill -9</c> or a
/// system-wide OOM event. Null when no such evidence is available (macOS, Windows Job Objects,
/// or Linux setrlimit-only enforcement), in which case callers fall back to exit-code heuristics alone.
/// </param>
/// <param name="AssignAfterStart">
/// Non-null on Windows when a Job Object was created in <see cref="IProcessResourceLimiter.Apply"/>
/// and must be bound to the live child via <c>AssignProcessToJobObject</c> immediately after
/// <see cref="Process.Start()"/> succeeds and <strong>before</strong> any stdout/stderr wait.
/// Returns a <see cref="ResourceLimitError"/> on failure (caller must kill the process tree and
/// treat the outcome as apply-failed — never leave the child running unbounded). Null on
/// macOS/Linux, where limits are applied inside the child before/at exec.
/// </param>
public sealed record ProcessResourceLimiterResult(
    ResourceLimitError? Error,
    Func<int, Task>? CleanupAsync,
    Func<Task<bool>>? WasOomKilledAsync = null,
    Func<Process, ResourceLimitError?>? AssignAfterStart = null);

/// <summary>
/// Applies OS-enforced resource limits (CPU time, memory, open file descriptors / process count)
/// to a child process. Implementations are platform-specific: setrlimit on macOS, cgroups v2
/// (with setrlimit fallback) on Linux, and Windows Job Objects (create in
/// <see cref="Apply"/>, assign immediately after <see cref="Process.Start()"/>).
/// </summary>
public interface IProcessResourceLimiter
{

    /// <summary>
    /// Applies <paramref name="limits"/> to <paramref name="startInfo"/> before the process is
    /// started. Implementations may rewrite <see cref="ProcessStartInfo.FileName"/> and
    /// <see cref="ProcessStartInfo.ArgumentList"/> to route the invocation through a shell prelude
    /// that applies limits inside the child process (Unix), or may allocate a Windows Job Object
    /// whose handle is returned via <see cref="ProcessResourceLimiterResult.AssignAfterStart"/>
    /// for post-start assignment (see remarks on the concrete implementation).
    /// </summary>
    ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits);

}
