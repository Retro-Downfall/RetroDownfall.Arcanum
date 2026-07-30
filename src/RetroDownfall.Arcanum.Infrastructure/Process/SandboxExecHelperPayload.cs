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

    public string[] ReadOnlyRoots { get; init; } = [];

}
