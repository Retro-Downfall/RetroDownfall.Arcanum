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
/// <see cref="ProcessStartInfo"/> before the child process starts. The message is safe to log;
/// it must never be surfaced verbatim to the model (callers wrap it in a generic denial).
/// </summary>
public sealed record ResourceLimitError(string Message);

/// <summary>
/// Result of attempting to apply <see cref="ResourceLimits"/> to a <see cref="ProcessStartInfo"/>.
/// </summary>
/// <param name="Error">Non-null when limits could not be applied; the process must not be started.</param>
/// <param name="CleanupAsync">
/// Non-null only when the limiter allocated OS-level state that must be torn down after the
/// child process exits (e.g. a transient Linux cgroups v2 directory). The caller must invoke this
/// with the started child's PID in a <c>finally</c> block once the process has exited. Null on
/// macOS, Windows, and the setrlimit/ulimit fallback paths, which have nothing to clean up.
/// </param>
public sealed record ProcessResourceLimiterResult(
    ResourceLimitError? Error,
    Func<int, Task>? CleanupAsync);

/// <summary>
/// Applies OS-enforced resource limits (CPU time, memory, open file descriptors) to a child
/// process before it starts. Implementations are platform-specific: setrlimit on macOS, cgroups v2
/// (with setrlimit fallback) on Linux, and a no-op on Windows.
/// </summary>
public interface IProcessResourceLimiter
{

    /// <summary>
    /// Applies <paramref name="limits"/> to <paramref name="startInfo"/> before the process is
    /// started. Implementations may rewrite <see cref="ProcessStartInfo.FileName"/> and
    /// <see cref="ProcessStartInfo.ArgumentList"/> to route the invocation through a shell prelude
    /// that applies limits inside the child process (see remarks on the concrete implementation).
    /// </summary>
    ProcessResourceLimiterResult Apply(ProcessStartInfo startInfo, ResourceLimits limits);

}
