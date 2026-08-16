using System.Collections.Frozen;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Central intrinsic safety classification for coding tools. Configured forbidden-arts lists may
/// add risk but cannot remove the tools that always require an operator Ward while Wards are on.
/// </summary>
public static class ToolRiskClassifier
{
    public const string ExecuteCommandToolName =
        HostProcessToolPolicy.ExecuteCommandToolName;

    public const string ApplyPatchToolName = "apply_patch";

    public const string WorkspaceCheckToolName = "workspace_check";

    public const string SearchWorkspaceToolName = "search_workspace";

    public const string ReadCommandOutputToolName =
        "read_command_output";

    /// <summary>
    /// Tools that always require an operator Ward while Wards are on, whatever the configured
    /// forbidden-arts list says.
    /// </summary>
    /// <remarks>
    /// <see cref="CovenantToolNames.RetireCovenant"/> is intrinsic because it deletes the operator's
    /// own standing instructions on the model's initiative. An operator who replaces the configurable
    /// list is choosing which tools they consider risky; they are not consenting to lose the prompt
    /// for that one (§10.14).
    /// </remarks>
    public static IReadOnlySet<string> IntrinsicWardToolNames { get; } =
        new[]
        {
            ExecuteCommandToolName,
            ApplyPatchToolName,
            WorkspaceCheckToolName,
            CovenantToolNames.RetireCovenant,
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsIntrinsicWardTool(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && IntrinsicWardToolNames.Contains(toolName);

    public static bool IsReadOnlyCodingTool(string? toolName) =>
        string.Equals(
            toolName,
            SearchWorkspaceToolName,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            ReadCommandOutputToolName,
            StringComparison.OrdinalIgnoreCase);

    public static string GetWardDisclosure(string? toolName) =>
        string.Equals(
            toolName,
            WorkspaceCheckToolName,
            StringComparison.OrdinalIgnoreCase)
            ? "This check executes workspace-authored code, including MSBuild tasks, source generators, analyzers, and tests. "
              + "The source workspace is mounted read-only, but server-created writable build, intermediate, CLI, temporary, and test-result roots are available. "
              + "The filesystem jail does not isolate network egress, so workspace code can exfiltrate readable source or package data. "
              + "Process-group and descendant cleanup are best effort; an intentionally malicious detached descendant may survive the check and continue network exfiltration. "
              + "Do not approve this check for an untrusted repository merely because its command arguments are fixed."
            : string.Empty;

    public static bool RequiresWard(
        string toolName,
        bool campaignRequiresWard,
        WardSettings wardSettings)
    {
        ArgumentNullException.ThrowIfNull(wardSettings);

        if (!wardSettings.Enabled)
        {
            return false;
        }

        if (IsIntrinsicWardTool(toolName))
        {
            return true;
        }

        return campaignRequiresWard
            && (wardSettings.ForbiddenArts?.Contains(toolName, StringComparer.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Whether the operator pre-authorized <paramref name="toolName"/> so the host may resolve its
    /// Ward without prompting (issue #53, DESIGN §11.14). This answers "who supplies consent", never
    /// "is this tool gated" — <see cref="RequiresWard"/> and the advertised tool set are unaffected,
    /// and every containment check still runs after an auto-approval. Fail-closed: off unless the
    /// operator both enables the policy and names the tool, and never while Wards are disabled
    /// (nothing is gated then, so there is no consent to supply).
    /// </summary>
    public static bool IsAutoApproved(string? toolName, WardSettings wardSettings)
    {
        ArgumentNullException.ThrowIfNull(wardSettings);

        if (!wardSettings.Enabled || !wardSettings.AutoApproveEnabled)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        IReadOnlyList<string> allowlist = wardSettings.AutoApproveTools;

        for (int i = 0; i < allowlist.Count; i++)
        {
            if (string.Equals(allowlist[i], toolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Names removed by <see cref="ToolPolicy.NoForbiddenArts"/>. The intrinsic set remains present
    /// even when an operator replaces the configurable forbidden-arts list.
    /// </summary>
    public static HashSet<string> BuildForbiddenToolNames(IEnumerable<string>? configuredNames)
    {
        HashSet<string> names = new(
            configuredNames ?? [],
            StringComparer.OrdinalIgnoreCase);

        names.UnionWith(IntrinsicWardToolNames);
        return names;
    }
}
