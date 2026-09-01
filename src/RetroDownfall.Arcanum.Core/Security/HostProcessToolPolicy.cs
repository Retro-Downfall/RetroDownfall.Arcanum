using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Intelligence;

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

    public const string RunSpellScriptToolName =
        ArcanumBuiltInToolNames.RunSpellScript;

    public const string AllowHostProcessToolsEnvVar = "ARCANUM_ALLOW_HOST_PROCESS_TOOLS";

    public const string DeniedMessage =
        "Host process tools (execute_command / run_spell_script) are disabled in the Local edition. "
        + "Set Arcanum:Edition=Development (or ARCANUM_EDITION=development) and "
        + "ARCANUM_ALLOW_HOST_PROCESS_TOOLS=1 to enable; GET /api/health will report Degraded.";

    /// <summary>The decision the startup gate published for this process, once it has one.</summary>
    /// <remarks>
    /// Every advertise and invoke site asks <see cref="AreAllowed"/>, and a static predicate cannot
    /// be handed a service. Binding the published policy here is what makes the gate's decision the
    /// one those sites read, instead of each of them re-deriving an answer from the same two inputs
    /// the gate has already refused (§10.12).
    /// </remarks>
    private static IHostProcessToolsRuntimePolicy? _startupDecision;

    public static bool IsHostProcessTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName)
        && (string.Equals(toolName, ExecuteCommandToolName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, RunSpellScriptToolName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Binds the decision the startup gate published as the one this process admits tools by.
    /// </summary>
    /// <remarks>
    /// Called once, by the host bootstrap, at the point the provisional classification becomes the
    /// process policy. A process that never runs the gate never binds one and keeps the edition and
    /// environment rule on its own — which is the honest answer there, because an unbound predicate
    /// has no gate decision to disagree with rather than a permissive one.
    /// </remarks>
    public static void BindStartupDecision(IHostProcessToolsRuntimePolicy policy)
    {

        ArgumentNullException.ThrowIfNull(policy);

        Volatile.Write(ref _startupDecision, policy);

    }

    /// <summary>Installs or clears the bound decision around a test that publishes one.</summary>
    /// <remarks>
    /// The binding is process-wide, so a test that boots a blocked host has to give it back or every
    /// later test in the run inherits a refusal it never asked for.
    /// </remarks>
    internal static void SetStartupDecisionForTests(IHostProcessToolsRuntimePolicy? policy) =>
        Volatile.Write(ref _startupDecision, policy);

    /// <summary>
    /// Resolves whether host process tools may be advertised or invoked for this process.
    /// </summary>
    public static bool AreAllowed(ArcanumEdition edition) =>
        edition == ArcanumEdition.Development
        && ArcanumEnvironment.IsAllowHostProcessToolsEnabled()
        && !RefusedByStartupGate();

    /// <summary>
    /// Whether the startup gate refused this installation's host-process-tools state.
    /// </summary>
    /// <remarks>
    /// Only a <i>blocked</i> publication subtracts. A gate that classified the installation without
    /// blocking computed its flag from the same edition and environment this predicate reads, so it
    /// can only disagree with them by being stale; a block is the gate's veto on an installation
    /// whose durable evidence says these tools must not be handed out, and that veto wins (§10.15).
    /// </remarks>
    private static bool RefusedByStartupGate()
    {

        IHostProcessToolsRuntimePolicy? published = Volatile.Read(ref _startupDecision);

        return published is { IsPublished: true, HostProcessToolsPermitted: false }
            && published.Blocker is not HostProcessToolsStartupBlocker.None;

    }

    /// <summary>
    /// Snapshot used by health / meta reporting.
    /// </summary>
    public static HostProcessToolPolicyStatus Resolve(ArcanumEdition edition)
    {
        bool envFlag = ArcanumEnvironment.IsAllowHostProcessToolsEnabled();
        bool allowed = edition == ArcanumEdition.Development && envFlag && !RefusedByStartupGate();

        string detail = allowed
            ? "Host process tools enabled (Development edition + ARCANUM_ALLOW_HOST_PROCESS_TOOLS). "
              + "This is an unsafe escape hatch — process is Degraded."
            : edition == ArcanumEdition.Development && envFlag
                ? "The startup gate refused host process tools for this process: this installation has "
                  + "no completed host-process-tools transition. Clear ARCANUM_ALLOW_HOST_PROCESS_TOOLS "
                  + "and start the host again."
                : edition == ArcanumEdition.Development
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
