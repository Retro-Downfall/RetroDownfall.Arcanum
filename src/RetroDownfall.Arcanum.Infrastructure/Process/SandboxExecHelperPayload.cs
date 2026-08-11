namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Payload for the hidden <c>arcanum __sandbox-exec --config …</c> helper (Linux Landlock).
/// </summary>
internal sealed class SandboxExecHelperPayload
{

    public string Target { get; init; } = string.Empty;

    public string[] Arguments { get; init; } = [];

    public string[] ReadWriteRoots { get; init; } = [];

    public string[] ReadExecuteRoots { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public string? WindowsProfileName { get; init; }

    /// <summary>
    /// Owner-only undo log the Windows broker appends to before each ACL mutation, so the host can
    /// restore the roots and delete the profile when the broker is killed before its own cleanup.
    /// </summary>
    public string? WindowsRestoreJournalPath { get; init; }

    public string[] ReadOnlyRoots { get; init; } = [];

}
