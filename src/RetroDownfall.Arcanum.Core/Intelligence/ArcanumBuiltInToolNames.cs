namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Canonical names for hub-native tools that must be recognized without consulting MCP discovery.
/// Only the three operator-local tools are exempt from artifact attunement; native web tools remain
/// attunable even though they are implemented in process.
/// </summary>
public static class ArcanumBuiltInToolNames
{
    public const string GetLocalSystemTime = "get_local_system_time";

    public const string GetArcanumSystemInfo = "get_arcanum_system_info";

    public const string RunSpellScript = "run_spell_script";

    public const string DelegateTask = "delegate_task";

    public const string WebSearch = "web_search";

    public const string ReadUrl = "read_url";

    /// <summary>
    /// Deprecated compatibility alias for <see cref="ReadUrl"/>. New tool catalogs must advertise
    /// only the canonical name.
    /// </summary>
    public const string BrowseWeb = "browse_web";

    public static bool IsKnown(string? toolName) =>
        IsAttunementExempt(toolName)
        || string.Equals(
            toolName,
            WebSearch,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            ReadUrl,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            BrowseWeb,
            StringComparison.OrdinalIgnoreCase);

    public static string Canonicalize(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        return string.Equals(toolName, BrowseWeb, StringComparison.OrdinalIgnoreCase)
            ? ReadUrl
            : toolName;
    }

    public static bool IsAttunementExempt(string? toolName) =>
        string.Equals(
            toolName,
            GetLocalSystemTime,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            GetArcanumSystemInfo,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            RunSpellScript,
            StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            toolName,
            DelegateTask,
            StringComparison.OrdinalIgnoreCase);
}
