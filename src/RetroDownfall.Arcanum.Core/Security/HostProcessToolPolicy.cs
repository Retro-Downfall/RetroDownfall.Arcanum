using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;

namespace RetroDownfall.Arcanum.Core.Security;

/// <summary>
/// Central policy for arbitrary host process execution tools (<c>execute_command</c>,
/// <c>run_spell_script</c>). Default Local edition denies advertise and invoke on every path.
/// Escape hatch: <see cref="ArcanumEdition.Development"/> plus startup env
/// <c>ARCANUM_ALLOW_HOST_PROCESS_TOOLS</c> — forces a degraded health component when active.
/// </summary>
public static class HostProcessToolPolicy
{

    public const string ExecuteCommandToolName = "execute_command";

    public const string RunSpellScriptToolName = "run_spell_script";

    public const string AllowHostProcessToolsEnvVar = "ARCANUM_ALLOW_HOST_PROCESS_TOOLS";

    public const string DeniedMessage =
        "Host process tools (execute_command / run_spell_script) are disabled in the Local edition. "
        + "Set Arcanum:Edition=Development (or ARCANUM_EDITION=development) and "
        + "ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1 to enable; GET /api/health will report Degraded.";

    public static bool IsHostProcessTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && (string.Equals(toolName, ExecuteCommandToolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, RunSpellScriptToolName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves whether host process tools may be advertised or invoked for this process.
    /// </summary>
    public static bool AreAllowed(ArcanumEdition edition) =>
        edition == ArcanumEdition.Development && ArcanumEnvironment.IsAllowHostProcessToolsEnabled();

    /// <summary>
    /// Snapshot used by health / meta reporting.
    /// </summary>
    public static HostProcessToolPolicyStatus Resolve(ArcanumEdition edition)
    {
        bool envFlag = ArcanumEnvironment.IsAllowHostProcessToolsEnabled();
        bool allowed = edition == ArcanumEdition.Development && envFlag;

        string detail = allowed
            ? "Host process tools enabled (Development edition + ARCANUM_ALLOW_HOST_PROCESS_TOOLS). "
              + "This is an unsafe escape hatch — process is Degraded."
            : edition == ArcanumEdition.Development && !envFlag
                ? "Development edition: host process tools remain off until ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1."
                : "Local edition: host process tools disabled (execute_command / run_spell_script).";

        return new HostProcessToolPolicyStatus(
            Edition: edition,
            Allowed: allowed,
            EscapeHatchEnvSet: envFlag,
            IsHealthDegraded: allowed,
            PublicMessage: detail);
    }

}

/// <param name="Edition">Resolved runtime edition.</param>
/// <param name="Allowed">Whether advertise/invoke of host process tools is permitted.</param>
/// <param name="EscapeHatchEnvSet">Whether <c>ARCANUM_ALLOW_HOST_PROCESS_TOOLS</c> is truthy.</param>
/// <param name="IsHealthDegraded">True when the escape hatch is active.</param>
/// <param name="PublicMessage">Operator-facing status text.</param>
public sealed record HostProcessToolPolicyStatus(
    ArcanumEdition Edition,
    bool Allowed,
    bool EscapeHatchEnvSet,
    bool IsHealthDegraded,
    string PublicMessage);
