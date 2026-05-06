namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Applies safe bounds when reading <see cref="ArcanumSettings"/> at runtime (invalid JSON or env overrides).
/// </summary>
public static class ArcanumSettingClamps
{

    public static int HostPort(int value) => Math.Clamp(value, 1, 65_535);

    public static int RetainedLogFileCount(int value) => Math.Clamp(value, 1, 366);

    public static int McpRequestTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int McpMaxPaginationPages(int value) => Math.Clamp(value, 1, 256);

    public static int ListDirectoryMaxPaths(int value) => Math.Clamp(value, 1, 100_000);

    public static int SemanticRouterMaxTokens(int value) => Math.Clamp(value, 1, 4096);

    public static float SemanticRouterTemperature(float value) => Math.Clamp(value, 0f, 2f);

    public static int MaxEnumerationSteps(int value) => Math.Clamp(value, 1, 10_000_000);

    public static int MaxTableOfContentsLines(int value) => Math.Clamp(value, 1, 500);

    public static long MaxAttachFileSizeBytes(long value) => Math.Clamp(value, 1024L, 100L * 1024L * 1024L);

}
