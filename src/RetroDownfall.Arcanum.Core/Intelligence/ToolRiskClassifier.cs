using System.Collections.Frozen;
using RetroDownfall.Arcanum.Core.Configuration;
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

    public static IReadOnlySet<string> IntrinsicWardToolNames { get; } =
        new[]
        {
            ExecuteCommandToolName,
            ApplyPatchToolName,
            WorkspaceCheckToolName,
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
