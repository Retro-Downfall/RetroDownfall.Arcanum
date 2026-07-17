namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Roots and policy for the MVP OS filesystem jail applied to tool children.
/// </summary>
/// <remarks>
/// This jail is filesystem-only. It does not prevent network use by network-capable binaries.
/// </remarks>
internal sealed class ChildProcessSandboxRequest
{

    /// <summary>Absolute roots granted read + write (+ execute when needed for script trees).</summary>
    internal required IReadOnlyList<string> ReadWriteRoots { get; init; }

    /// <summary>Absolute roots granted read + execute only (system runtime, interpreters).</summary>
    internal required IReadOnlyList<string> ReadExecuteRoots { get; init; }

    /// <summary>
    /// When true, a missing/unusable sandbox logs a warning and proceeds without FS confinement.
    /// When false (default), setup failure returns <see cref="CappedChildProcessOutcome.FilesystemSandboxUnavailable"/>.
    /// </summary>
    internal bool AllowUnsandboxed { get; init; }

    /// <summary>
    /// When true on Windows, tool children are denied (no reliable FS jail). Ignored on Linux/macOS.
    /// </summary>
    internal bool WindowsPathBoundaryRequired { get; init; }

}
