namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Roots and policy for the MVP OS filesystem jail applied to tool children.
/// </summary>
/// <remarks>
/// This jail is filesystem-only. It does not prevent network use by network-capable binaries.
/// On Windows without Sanctum path-boundary, status is <c>NoFilesystemJail</c> (Job Objects only).
/// </remarks>
internal sealed class ChildProcessSandboxRequest
{

    /// <summary>Absolute roots granted read + write.</summary>
    internal required IReadOnlyList<string> ReadWriteRoots { get; init; }

    /// <summary>Absolute roots granted read-only access; execution is not granted by this class.</summary>
    internal IReadOnlyList<string> ReadOnlyRoots { get; init; } = [];

    /// <summary>Absolute roots granted read + execute only (system runtime, spell script trees).</summary>
    internal required IReadOnlyList<string> ReadExecuteRoots { get; init; }

    /// <summary>
    /// When true, a missing/unusable sandbox logs a warning and proceeds without FS confinement.
    /// When false (default), setup failure returns <see cref="CappedChildProcessOutcome.FilesystemSandboxUnavailable"/>.
    /// Does not bypass Windows Sanctum path-boundary denial.
    /// </summary>
    internal bool AllowUnsandboxed { get; init; }

    /// <summary>
    /// When true on Windows, tool children are denied (no reliable FS jail). Ignored on Linux/macOS.
    /// </summary>
    internal bool WindowsPathBoundaryRequired { get; init; }

    /// <summary>
    /// When true, only an actively applied filesystem jail may proceed. No operator escape hatch,
    /// Windows no-jail posture, or other non-applied status satisfies this requirement.
    /// </summary>
    internal bool RequireAppliedFilesystemJail { get; init; }

    /// <summary>Tool name for escape-hatch / denial diagnostics (no secrets).</summary>
    internal string? ToolName { get; init; }

    /// <summary>Optional campaign id for escape-hatch logging.</summary>
    internal string? CampaignIdForLog { get; init; }

    /// <summary>Optional workspace root for escape-hatch logging (redacted to leaf name).</summary>
    internal string? WorkspaceRootForLog { get; init; }

    /// <summary>Canonical source root used to classify a denied sandbox write.</summary>
    internal string? SourceReadOnlyRoot { get; init; }

    /// <summary>Canonical package-cache root used to classify a denied sandbox write.</summary>
    internal string? PackageReadOnlyRoot { get; init; }

}
