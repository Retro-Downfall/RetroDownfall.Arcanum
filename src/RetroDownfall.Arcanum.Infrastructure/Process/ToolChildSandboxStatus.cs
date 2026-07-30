namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>Filesystem jail posture for tool children (<c>execute_command</c> / <c>run_spell_script</c>).</summary>
public enum ToolChildFilesystemJailMode
{

    /// <summary>OS filesystem jail is applied (e.g. macOS Seatbelt via <c>/usr/bin/sandbox-exec</c>).</summary>
    Active,

    /// <summary>Jail required but not active; tools fail closed unless the escape hatch is enabled.</summary>
    InactiveFailClosed,

    /// <summary>Operator escape hatch: running without FS confinement.</summary>
    UnsandboxedEscapeHatch,

    /// <summary>No FS jail is available on this platform.</summary>
    NotAvailable,

}

/// <summary>OS resource-limit posture for tool children (rlimits / cgroups / Job Objects).</summary>
public enum ToolChildResourceLimitsMode
{

    Active,

    Partial,

    NotAvailable,

}

/// <summary>Network isolation for tool children — not provided in this beta.</summary>
public enum ToolChildNetworkIsolationMode
{

    NotProvided,

}

/// <summary>
/// Operator-facing capability snapshot for tool-child confinement.
/// Not a wire DTO; used by <c>arcanum doctor</c> and <c>GET /api/health</c>.
/// </summary>
public sealed class ToolChildSandboxStatus
{

    public required string Platform { get; init; }

    public required ToolChildFilesystemJailMode FilesystemJailMode { get; init; }

    public required ToolChildResourceLimitsMode ResourceLimitsMode { get; init; }

    public required ToolChildNetworkIsolationMode NetworkIsolationMode { get; init; }

    public required string PublicMessage { get; init; }

    public required string OperatorGuidance { get; init; }

    public required bool IsBetaSafeDefault { get; init; }

    public required bool EscapeHatchEnabled { get; init; }

    /// <summary>
    /// Maps to health component status: Healthy only for active jail or equivalent safe state;
    /// Degraded for Windows no-FS-jail, Linux inactive fail-closed, escape hatch, or missing macOS sandbox-exec.
    /// </summary>
    public required bool IsHealthDegraded { get; init; }

}
