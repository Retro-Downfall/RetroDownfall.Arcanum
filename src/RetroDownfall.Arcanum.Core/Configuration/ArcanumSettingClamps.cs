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

    public static int ListDirectoryMaxPaths(int value) => Math.Clamp(value, 1, 2_000);

    public static int ListDirectoryMaxDepth(int value) => Math.Clamp(value, 1, 256);

    public static int SemanticRouterMaxTokens(int value) => Math.Clamp(value, 1, 4096);

    public static float SemanticRouterTemperature(float value) => Math.Clamp(value, 0f, 2f);

    public static int MaxEnumerationSteps(int value) => Math.Clamp(value, 1, 10_000_000);

    public static int MaxTableOfContentsLines(int value) => Math.Clamp(value, 1, 500);

    public static long MaxAttachFileSizeBytes(long value) => Math.Clamp(value, 1024L, 100L * 1024L * 1024L);

    public static long CodexMaxSizeBytes(long value) => Math.Clamp(value, 1024L, 1024L * 1024L);

    public static long SpellMaxFileSizeBytes(long value) => Math.Clamp(value, 1024L, 1024L * 1024L);

    public static long EffectiveSpellMaxFileSizeBytes(SpellSettings spells, WorkspaceSettings workspaces)
    {
        long spell = SpellMaxFileSizeBytes(spells.MaxFileSizeBytes);

        long workspace = MaxFileReadSizeBytes(workspaces.MaxFileReadSizeBytes);

        return Math.Min(spell, workspace);
    }

    public static long EffectiveCodexMaxSizeBytes(CodexSettings codex, WorkspaceSettings workspaces)
    {
        long codexMax = CodexMaxSizeBytes(codex.MaxSizeBytes);

        long workspace = MaxFileReadSizeBytes(workspaces.MaxFileReadSizeBytes);

        return Math.Min(codexMax, workspace);
    }

    public static long EffectiveSpellMaxFileSizeBytes(ArcanumSettings settings) =>
        EffectiveSpellMaxFileSizeBytes(settings.Spells, settings.Workspaces);

    public static long EffectiveCodexMaxSizeBytes(ArcanumSettings settings) =>
        EffectiveCodexMaxSizeBytes(settings.Codex, settings.Workspaces);

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

    public static int DaemonExecutionHistoryLimit(int value) => Math.Clamp(value, 10, 10_000);

    public static int MaxToolInferenceRounds(int value) => Math.Clamp(value, 1, 64);

    public static int CompressionPreflightMinMessages(int value) => Math.Clamp(value, 0, 100);

    public static int PerMessageTemplateOverheadTokens(int value) => Math.Clamp(value, 0, 32);

    public static int WebhookTimeoutSeconds(int value) => Math.Clamp(value, 1, 120);

    public static long MaxRequestBodyBytes(long value) => Math.Clamp(value, 256L * 1024L, 1024L * 1024L * 1024L);

    public static int RateLimitPermitLimit(int value) => Math.Clamp(value, 1, 1_000_000);

    public static int RateLimitWindowSeconds(int value) => Math.Clamp(value, 1, 86_400);

    public static int RateLimitQueueLimit(int value) => Math.Clamp(value, 0, 1_000_000);

    public static int MaxMessagesPerConversationLoad(int value) => Math.Clamp(value, 50, 5_000);

    public static int ListQueryLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static int WorkspaceContextRetentionCount(int value) => Math.Clamp(value, 1, 1_000);

    public static int DoctorHealthTimeoutSeconds(int value) => Math.Clamp(value, 1, 60);

    public static int EventBusChannelCapacity(int value) => Math.Clamp(value, 64, 65_536);

    public static int LogRingBufferCapacity(int value) => Math.Clamp(value, 1000, 100_000);

    public static int LogQueryLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static long MaxFileReadSizeBytes(long value) => Math.Clamp(value, 1024, 10 * 1024 * 1024);

    public static int SessionQueryLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static int SessionStreamReplayLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static int LlamaGpuLayers(int value) => Math.Clamp(value, -1, 1024);

    public static int LlamaContextSize(int value) => Math.Clamp(value, 256, 1_048_576);

    public static int LlamaPortStart(int value) => Math.Clamp(value, 1, 65_535);

    public static int LlamaPortRange(int value) => Math.Clamp(value, 1, 65_535);

    public static int LlamaMaxConcurrentRequests(int value) => Math.Clamp(value, 1, 256);

    public static int LlamaHealthProbeTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int LlamaStartTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int LlamaShutdownTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int LlamaMaxCachedModels(int value) => Math.Clamp(value, 1, 100);

    public static int LlamaModelDownloadTimeoutSeconds(int value) => Math.Clamp(value, 60, 86_400);

    public static long LlamaModelDownloadMaxBytes(long value) => Math.Clamp(value, 1024L * 1024L, 200L * 1024L * 1024L * 1024L);

    public static int MaxCampaigns(int value) => Math.Clamp(value, 10, 10_000);

    public static int WardTimeoutSeconds(int value) => Math.Clamp(value, 10, 600);

    public static int MaxConcurrentApprentices(int value) => Math.Clamp(value, 1, 50);

    public static int StepTimeoutMinutes(int value) => Math.Clamp(value, 5, 120);

    public static int ChronicleChannelCapacity(int value) => Math.Clamp(value, 100, 10_000);

    public static int SanctumMaxProcessMemoryMb(int value) => Math.Clamp(value, 64, 8192);

    public static int SanctumMaxProcessCount(int value) => Math.Clamp(value, 1, 100);

    public static int SanctumMaxFileWriteMb(int value) => Math.Clamp(value, 1, 1024);

    public static int SanctumProcessTimeoutSeconds(int value) => Math.Clamp(value, 10, 3600);

    public static int SanctumBreachQueryLimit(int value) => Math.Clamp(value, 1, 1000);

}

