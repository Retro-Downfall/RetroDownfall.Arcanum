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
    /// <c>run_spell_script</c>: inherit the host environment unchanged.
    /// </summary>
    SpellScript,

}
