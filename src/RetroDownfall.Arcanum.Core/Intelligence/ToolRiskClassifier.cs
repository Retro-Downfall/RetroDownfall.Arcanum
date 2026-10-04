using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Central classification for coding-tool capabilities.
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
              + "Do not run this check for an untrusted repository merely because its command arguments are fixed."
            : string.Empty;

    /// <summary>
    /// Operator-configured names removed from advertisement by <see cref="ToolPolicy.NoForbiddenArts"/>.
    /// </summary>
    public static HashSet<string> BuildForbiddenToolNames(IEnumerable<string>? configuredNames)
    {
        HashSet<string> names = new(
            configuredNames ?? [],
            StringComparer.OrdinalIgnoreCase);

        return names;
    }
}
