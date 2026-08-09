namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

/// <summary>
/// Named child-process environment policies used across Arcanum subprocess spawns.
/// </summary>
public enum ChildProcessEnvironmentProfile
{

    /// <summary>
    /// MCP workspace servers: full scrub via <see cref="Mcp.McpSecurityLimits"/> (explicit env block).
    /// </summary>
    McpChild,

    /// <summary>
    /// <c>execute_command</c>: strip <c>ARCANUM_*</c> secrets only; preserve PATH, HOME, and other host vars.
    /// </summary>
    ToolExec,

    /// <summary>
    /// <c>run_spell_script</c>: same scrub as <see cref="ToolExec"/> — strip <c>ARCANUM_*</c>
    /// secrets and loader/runtime hijack variables; preserve PATH/HOME and other host vars.
    /// </summary>
    SpellScript,

    /// <summary>
    /// <c>workspace_check</c>: the caller clears and reconstructs the complete environment from
    /// a small OS/runtime allowlist before invoking the runner.
    /// </summary>
    WorkspaceCheck,

    /// <summary>
    /// A Familiar (the operator's installed <c>claude</c> / <c>codex</c> CLI): same scrub as
    /// <see cref="ToolExec"/> — strip <c>ARCANUM_*</c> secrets and loader/runtime hijack variables —
    /// keeping PATH and HOME, because the CLI resolves its own runtime and reads its own auth store
    /// exactly the way the operator's shell does. The caller additionally names any configured
    /// provider credential variables to strip.
    /// </summary>
    Familiar,

}
