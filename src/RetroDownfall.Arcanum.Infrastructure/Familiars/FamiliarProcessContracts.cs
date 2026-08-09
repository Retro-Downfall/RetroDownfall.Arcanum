namespace RetroDownfall.Arcanum.Infrastructure.Familiars;

/// <summary>
/// One invocation of a Familiar — the operator's installed vendor CLI. The shape is deliberately
/// narrow: a binary, a fixed argument list, and a prompt on stdin. There is no command string and
/// no shell anywhere in the contract, because a single <c>command + args</c> string is the prefix
/// and injection hazard this transport must never reintroduce.
/// </summary>
public sealed record FamiliarProcessRequest
{

    /// <summary>The binary to spawn: an operator override, or a bare name resolved through PATH.</summary>
    public required string FileName { get; init; }

    /// <summary>Arcanum-authored arguments, handed to the OS one element at a time.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Written to the child's stdin, which is then closed so the CLI stops waiting.</summary>
    public string? StandardInput { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Extra environment-variable names to remove from the child, on top of the standard scrub.
    /// Arcanum's configured provider credential references land here: they are Arcanum's secrets to
    /// hold, not the Familiar's to receive.
    /// </summary>
    public IReadOnlyList<string> DeniedEnvironmentVariables { get; init; } = [];

    /// <summary>
    /// Wall-clock ceiling for the whole invocation. A wedged CLI must never hold a request open, so
    /// the deadline is mandatory in practice — the default is generous, not absent.
    /// </summary>
    public TimeSpan Timeout { get; init; } = FamiliarProcessLimits.DefaultTimeout;

}

/// <summary>
/// Why a Familiar invocation did not produce a clean result. Every value is actionable on its own:
/// there is no generic "failed" that leaves the operator guessing whether they need to install
/// something, sign in, or look at the CLI's own output.
/// </summary>
public enum FamiliarProcessFailure
{

    None,

    /// <summary>The binary is not where Arcanum was told to look, or not on PATH.</summary>
    NotInstalled,

    /// <summary>The binary exists but the OS refused to start it.</summary>
    StartFailed,

    /// <summary>The CLI ran and rejected the invocation.</summary>
    NonZeroExit,

    /// <summary>The CLI exceeded its deadline and its process tree was killed.</summary>
    TimedOut,

}

/// <summary>Buffered outcome, used where a classification is wanted instead of an exception.</summary>
public sealed record FamiliarProcessOutput(
    FamiliarProcessFailure Failure,
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// A Familiar invocation that failed closed. Carries the CLI's own stderr tail because that text is
/// the operator's most useful diagnostic — the CLI knows why it refused, and Arcanum does not.
/// </summary>
public sealed class FamiliarProcessException(
    FamiliarProcessFailure failure,
    string message,
    int exitCode = 0,
    string standardError = "") : Exception(message)
{

    public FamiliarProcessFailure Failure { get; } = failure;

    public int ExitCode { get; } = exitCode;

    public string StandardError { get; } = standardError;

}

/// <summary>
/// Code-owned envelopes for a Familiar spawn.
/// </summary>
/// <remarks>
/// Deliberately <c>internal</c> as well as absent from <c>arcanum.json</c>. These are implementation
/// mechanics — timeouts and buffer ceilings — which the strict inclusion rule (DESIGN §3.4) keeps
/// code-owned, and keeping them off the public surface keeps them out of the public-limit contract
/// inventory too, where they would read as operator policy.
/// </remarks>
internal static class FamiliarProcessLimits
{

    /// <summary>
    /// Long enough for a reasoning-heavy turn on a slow link, short enough that a wedged CLI is
    /// eventually reaped rather than holding an HTTP request forever.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A status probe answers immediately or not at all.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// One NDJSON frame. Generous because a session-init frame carries the CLI's whole capability
    /// inventory, but bounded so a runaway line cannot exhaust memory.
    /// </summary>
    public const int MaxLineCharacters = 4 * 1024 * 1024;

    /// <summary>Enough stderr to explain a refusal; the rest is dropped rather than buffered.</summary>
    public const int MaxStandardErrorCharacters = 64 * 1024;

    /// <summary>Buffered stdout ceiling for the probe path.</summary>
    public const int MaxBufferedStandardOutputCharacters = 1024 * 1024;

}
