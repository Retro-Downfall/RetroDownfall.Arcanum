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

    public static int MaxApiKeyHeaderUtf16Chars(int value) => Math.Clamp(value, 128, 8192);

    public static int MaxAttachedFilesPerRequest(int value) => Math.Clamp(value, 1, 256);

    public static int MaxAttachedFileRelativePathChars(int value) => Math.Clamp(value, 256, 8192);

    public static int ArchiveSearchMaxQueryLength(int value) => Math.Clamp(value, 32, 4096);

    public static int UnseenServantIntervalMinutes(int value) => Math.Clamp(value, 1, 10_080);

    public static int CampaignLogThreshold(int value) => Math.Clamp(value, 1, 10_000);

    public static int CampaignLogIdleTimeoutMinutes(int value) => Math.Clamp(value, 1, 43_200);

    public static int CampaignLogSweepIntervalMinutes(int value) => Math.Clamp(value, 1, 1_440);

    public static int ArchiveSearchMaxResults(int value) => Math.Clamp(value, 1, 100);

    public static int ContextWindowLimit(int value) => Math.Clamp(value, 256, 2_097_152);

    public static int ExecuteCommandTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int SemanticRouterPreflightTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int ContextWindowCompressionThreshold(int value) => Math.Clamp(value, 50, 100);

    public static int ApiKeyCacheTtlSeconds(int value) => Math.Clamp(value, 1, 3_600);

    public static long ToolOutputCapBytes(long value) => Math.Clamp(value, 64L * 1024L, 64L * 1024L * 1024L);

    public static int DaemonMaxConcurrentJobs(int value) => Math.Clamp(value, 1, 1_024);

    public static int DaemonShutdownDrainTimeoutSeconds(int value) => Math.Clamp(value, 0, 600);

}

