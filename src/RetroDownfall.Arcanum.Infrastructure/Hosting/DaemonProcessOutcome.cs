using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Outcome of a daemon helper process invocation. <see cref="FatalError"/> is set only when the helper
/// binary could not be run to completion at all (missing from PATH, not executable, access denied, or
/// killed at the runner's timeout), which is distinct from the binary running and reporting a non-zero
/// <see cref="ExitCode"/>. <see cref="AccessDenied"/> is true when that failure was the OS refusing the
/// caller access, which the Windows manager reports as an elevation requirement.
/// </summary>
internal sealed record DaemonProcessOutcome(
    int ExitCode,
    string StdOut,
    string StdErr,
    Error? FatalError,
    bool AccessDenied = false);
