namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// Canonical names for hub-native tools that must be recognized without consulting MCP discovery.
/// Only the three operator-local tools are exempt from artifact attunement; web browsing remains
/// attunable even though it is implemented in process.
/// </summary>
public static class ArcanumBuiltInToolNames
{
    public const string GetLocalSystemTime = "get_local_system_time";

    public const string GetArcanumSystemInfo = "get_arcanum_system_info";

    public const string RunSpellScript = "run_spell_script";

    public const string BrowseWeb = "browse_web";

    public static bool IsKnown(string? toolName) =>
        IsAttunementExempt(toolName)
        || string.Equals(
            toolName,
            BrowseWeb,
            StringComparison.OrdinalIgnoreCase);

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
            StringComparison.OrdinalIgnoreCase);
}
