using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

/// <summary>
/// Outcome of a daemon helper process invocation. <see cref="FatalError"/> is set only when the helper
/// binary could not be started at all (missing from PATH, not executable, or access denied), which is
/// distinct from the binary running and reporting a non-zero <see cref="ExitCode"/>.
/// </summary>
internal sealed record DaemonProcessOutcome(int ExitCode, string StdOut, string StdErr, Error? FatalError);
