namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Roots and policy for the MVP OS filesystem jail applied to tool children.
/// </summary>
/// <remarks>
/// This jail is filesystem-only unless <see cref="DenyNetwork"/> is set; without it, it does not prevent
/// network use by network-capable binaries.
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

    /// <summary>
    /// When true, the child runs with no network: the macOS profile grants no network operation and no
    /// socket, and the Windows AppContainer grants no capability (as it never does). Only an applied jail
    /// can deliver that, so the runner refuses the child whenever the jail is not applied, whatever
    /// <see cref="AllowUnsandboxed"/> says. Off by default and in every model-directed tool builder.
    /// </summary>
    internal bool DenyNetwork { get; init; }

    /// <summary>
    /// When true, the child is one process: the macOS profile refuses <c>process-fork</c> (fork, vfork and posix_spawn)
    /// while still letting the launch chain exec the target. Windows already bounds the tree by the Job Object's
    /// active-process limit, so the flag changes nothing there. Off by default and in every model-directed tool builder,
    /// whose children fork their own tools.
    /// </summary>
    internal bool DenyProcessFork { get; init; }

    /// <summary>
    /// When true, the child lists directories and reads file metadata only within its roots: the macOS profile leaves out
    /// its global directory-listing and metadata grants and adds back only each root's ancestors (metadata, for path
    /// resolution), the root directory's own entry and the <c>/bin/sh</c> selector link. The Windows AppContainer already
    /// reaches nothing it was not granted, so the flag changes nothing there. Off by default and in every model-directed
    /// tool builder.
    /// </summary>
    internal bool ConfineMetadataToRoots { get; init; }

    /// <summary>
    /// When true, the child writes no file anywhere. It may name no <see cref="ReadWriteRoots"/>: the jail refuses that
    /// contradiction outright, with no operator escape. The run's private temp directory is still created, named by the
    /// temp variables and removed by the host, but the child may only read it; on macOS the profile grants no write, no
    /// socket, clone or link anywhere, and of the devices only <c>/dev/null</c> for writing; on Windows the temp directory
    /// is granted to the AppContainer read-only. Only an applied jail delivers it, so a caller pairs it with
    /// <see cref="RequireAppliedFilesystemJail"/>, as the document worker does. Off by default and in every model-directed
    /// tool builder, whose children write their workspace and temp directory.
    /// </summary>
    internal bool DenyFileWrites { get; init; }

    /// <summary>
    /// When true, the child can read no other process's arguments, environment or path and cannot list the processes: the
    /// macOS profile reads only the sysctls the worker's runtime needs and writes none, and refuses every <c>process-info*</c>
    /// operation on a process other than the child itself, so another process's argument vector and environment
    /// (<c>KERN_PROCARGS2</c>), executable path (<c>proc_pidpath</c>) and entry, and the process list (<c>KERN_PROC_ALL</c>,
    /// <c>proc_listpids</c>) are all refused. The flag changes nothing on
    /// Windows. Off by default and in every model-directed tool builder, whose children read sysctls freely and inspect the
    /// processes they start.
    /// </summary>
    internal bool ConfineProcessInformation { get; init; }

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
