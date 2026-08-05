namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Applies safe bounds when reading <see cref="ArcanumSettings"/> at runtime (invalid JSON or env overrides).
/// </summary>
public static class ArcanumSettingClamps
{

    public static int HostPort(int value) => Math.Clamp(value, 1, 65_535);

    public static int HostHttpsPort(int value) => Math.Clamp(value, 1, 65_535);

    public static int RetainedLogFileCount(int value) => Math.Clamp(value, 1, 366);

    public static int RetentionSweepIntervalHours(int value) => Math.Clamp(value, 1, 168);

    public static int RetentionAccountingMinimumDays(int value) => Math.Clamp(value, 30, 3_650);

    public static int RetentionRuleDays(int value) => Math.Clamp(value, 1, 3_650);

    public static int McpInitializationTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int SemanticRouterMaxTokens(int value) => Math.Clamp(value, 1, 4096);

    public static int LexiconMaxMatchedEntries(int value) => Math.Clamp(value, 1, 100);

    public static int LexiconMaxInjectedBytes(int value) => Math.Clamp(value, 256, 65_536);

    public static float SemanticRouterTemperature(float value) => Math.Clamp(value, 0f, 2f);

    public static int MaxTableOfContentsLines(int value) => Math.Clamp(value, 1, 500);

    public static long MaxAttachFileSizeBytes(long value) => Math.Clamp(value, 1024L, 100L * 1024L * 1024L);

    public static long CodexMaxSizeBytes(long value) => Math.Clamp(value, 1024L, 1024L * 1024L);

    public static long SpellMaxFileSizeBytes(long value) => Math.Clamp(value, 1024L, 1024L * 1024L);

    public static long EffectiveSpellMaxFileSizeBytes(SpellSettings spells, WorkspaceSettings workspaces)
    {
        long spell = SpellMaxFileSizeBytes(spells.MaxFileSizeBytes);

        long workspace = MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        return Math.Min(spell, workspace);
    }

    public static long EffectiveCodexMaxSizeBytes(CodexSettings codex, WorkspaceSettings workspaces)
    {
        long codexMax = CodexMaxSizeBytes(codex.MaxSizeBytes);

        long workspace = MaxFileReadSizeBytes(
            ArcanumRuntimeDefaults.WorkspaceMaxFileReadSizeBytes);

        return Math.Min(codexMax, workspace);
    }

    public static long EffectiveSpellMaxFileSizeBytes(ArcanumSettings settings) =>
        EffectiveSpellMaxFileSizeBytes(ArcanumRuntimeDefaults.Spells, settings.Workspaces);

    public static long EffectiveCodexMaxSizeBytes(ArcanumSettings settings) =>
        EffectiveCodexMaxSizeBytes(ArcanumRuntimeDefaults.Codex, settings.Workspaces);

    public static int MaxApiKeyHeaderUtf16Chars(int value) => Math.Clamp(value, 128, 8192);

    public static int MaxAttachedFileRelativePathChars(int value) => Math.Clamp(value, 256, 8192);

    public static int ArchiveSearchMaxQueryLength(int value) => Math.Clamp(value, 32, 4096);

    public static int UnseenServantIntervalMinutes(int value) => Math.Clamp(value, 1, 10_080);

    public static int CampaignLogThreshold(int value) => Math.Clamp(value, 1, 10_000);

    public static int CampaignLogIdleTimeoutMinutes(int value) => Math.Clamp(value, 1, 43_200);

    public static int CampaignLogSweepIntervalMinutes(int value) => Math.Clamp(value, 1, 1_440);

    public static int ArchiveSearchMaxResults(int value) => Math.Clamp(value, 1, 100);

    public static int ContextWindowLimit(int value) => Math.Clamp(value, 256, 2_097_152);

    public static int SessionMaxPinnedEntries(int value) => Math.Clamp(value, 0, 100);

    public static int ReasoningBudgetTokens(int value) => Math.Clamp(value, 1, 2_097_152);

    public static int WorkspaceSearchMaxPatternChars(int value) => Math.Clamp(value, 1, 16_384);

    public static int WorkspaceSearchRegexTimeoutMilliseconds(int value) => Math.Clamp(value, 10, 10_000);

    public static int WorkspaceSearchMaxPreviewChars(int value) => Math.Clamp(value, 16, 4_096);

    public static long WorkspacePatchMaxPatchBytes(long value) => Math.Clamp(value, 1_024L, 64L * 1024L * 1024L);

    public static long WorkspacePatchMaxInputBytesPerFile(long value) =>
        Math.Clamp(value, 1_024L, 256L * 1024L * 1024L);

    public static long WorkspacePatchMaxOutputBytesPerFile(long value) =>
        Math.Clamp(value, 1_024L, 256L * 1024L * 1024L);

    public static long WorkspacePatchMaxTotalOutputBytes(long value) =>
        Math.Clamp(value, 1_024L, 1L * 1024L * 1024L * 1024L);

    public static long WorkspacePatchMaxStagingBytesPerFile(long value) =>
        Math.Clamp(value, 1_024L, 512L * 1024L * 1024L);

    public static long WorkspacePatchMaxTotalStagingBytes(long value) =>
        Math.Clamp(value, 1_024L, 2L * 1024L * 1024L * 1024L);

    public static int WorkspacePatchRecoveryTimeoutMilliseconds(int value) =>
        Math.Clamp(value, 50, 60_000);

    public static int WorkspacePatchFuzzyMatchWindowLines(int value) => Math.Clamp(value, 0, 1_000);

    public static WorkspacePatchSettings NormalizeWorkspacePatchSettings(
        WorkspacePatchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new WorkspacePatchSettings
        {
            MaxPatchBytes =
                WorkspacePatchMaxPatchBytes(
                    settings.MaxPatchBytes),
            MaxInputBytesPerFile =
                WorkspacePatchMaxInputBytesPerFile(
                    settings.MaxInputBytesPerFile),
            MaxOutputBytesPerFile =
                WorkspacePatchMaxOutputBytesPerFile(
                    settings.MaxOutputBytesPerFile),
            MaxTotalOutputBytes =
                WorkspacePatchMaxTotalOutputBytes(
                    settings.MaxTotalOutputBytes),
            MaxStagingBytesPerFile =
                WorkspacePatchMaxStagingBytesPerFile(
                    settings.MaxStagingBytesPerFile),
            MaxTotalStagingBytes =
                WorkspacePatchMaxTotalStagingBytes(
                    settings.MaxTotalStagingBytes),
            RecoveryTimeoutMilliseconds =
                WorkspacePatchRecoveryTimeoutMilliseconds(
                    settings.RecoveryTimeoutMilliseconds),
            FuzzyMatchWindowLines =
                WorkspacePatchFuzzyMatchWindowLines(
                    settings.FuzzyMatchWindowLines),
        };
    }

    public const int WorkspaceCheckCleanupGraceSeconds = 30;

    public static int WorkspaceCheckMaxCustomProfiles(int value) => Math.Clamp(value, 0, 256);

    public static int WorkspaceCheckMaxFixedArgumentsPerProfile(int value) => Math.Clamp(value, 1, 128);

    public static int WorkspaceCheckMaxArgumentTokenChars(int value) => Math.Clamp(value, 16, 4_096);

    public static int WorkspaceCheckMaxOptionsPerProfile(int value) => Math.Clamp(value, 0, 64);

    public static int WorkspaceCheckMaxAllowedValuesPerOption(int value) => Math.Clamp(value, 1, 128);

    public static int WorkspaceCheckMaxDiagnostics(int value) => Math.Clamp(value, 1, 10_000);

    public static long WorkspaceCheckMaxOutputBytes(long value) => Math.Clamp(value, 4_096L, 64L * 1024L * 1024L);

    public static int SemanticRouterPreflightTimeoutSeconds(int value) => Math.Clamp(value, 1, 600);

    public static int ContextWindowCompressionThreshold(int value) => Math.Clamp(value, 50, 100);

    public static int ApiKeyCacheTtlSeconds(int value) => Math.Clamp(value, 1, 3_600);

    public static int SecurityIdempotencyTtlHours(int value) => Math.Clamp(value, 1, 168);

    public static int SecurityIdempotencyMaxResponseBytes(int value) => Math.Clamp(value, 1024 * 1024, 100 * 1024 * 1024);

    /// <summary>Hard cap on the raw <c>Idempotency-Key</c> header value length (not configurable — a fixed guardrail, not a tunable setting).</summary>
    public const int SecurityIdempotencyKeyMaxChars = 256;

    public static long FilesMaxUploadSizeBytes(long value) => Math.Clamp(value, 1024L * 1024L, 10L * 1024L * 1024L * 1024L);

    public static long AttachmentsMaxBytesPerSession(long value) => Math.Clamp(value, 1024L * 1024L, 10L * 1024L * 1024L * 1024L);

    public static int AttachmentsPendingRetentionHours(int value) => Math.Clamp(value, 1, 168);

    public static int AttachmentsMaxIndexItemsInPrompt(int value) => Math.Clamp(value, 1, 200);

    public static int AttachmentsMaxIndexBytesInPrompt(int value) => Math.Clamp(value, 256, 64_000);

    public static int BatchesMaxConcurrentBatches(int value) => Math.Clamp(value, 1, 20);

    public static int BatchesMaxConcurrentRequestsPerBatch(int value) => Math.Clamp(value, 1, 10);

    public static long ToolOutputCapBytes(long value) => Math.Clamp(value, 64L * 1024L, 64L * 1024L * 1024L);

    public const int JsonRpcEnvelopeUtf8MarginBytes = 8_192;

    public const int JsonRpcMaxEscapingFactor = 2;

    public static long EffectiveInProcessToolOutputCapBytes(long toolOutputCapBytes, int maxJsonRpcLineBytes)
    {

        long configuredCap = ToolOutputCapBytes(toolOutputCapBytes);

        long lineBudget = Math.Max(0L, maxJsonRpcLineBytes - JsonRpcEnvelopeUtf8MarginBytes);

        long escapedBudget = lineBudget / JsonRpcMaxEscapingFactor;

        return Math.Min(configuredCap, escapedBudget);

    }

    public static int DaemonMaxConcurrentJobs(int value) => Math.Clamp(value, 1, 1_024);

    public static int DaemonShutdownDrainTimeoutSeconds(int value) => Math.Clamp(value, 0, 600);

    public static int DaemonExecutionHistoryLimit(int value) => Math.Clamp(value, 10, 10_000);

    /// <summary>Tokens reserved for model output during context preflight when MaxOutputTokens is unset.</summary>
    public static int ReservedOutputTokens(int value) => Math.Clamp(value, 0, 128_000);

    public static int CompressionPreflightMinMessages(int value) => Math.Clamp(value, 0, 100);

    public static int PerMessageTemplateOverheadTokens(int value) => Math.Clamp(value, 0, 32);

    public static int EstimatedTokenSafetyMarginPercent(int value) => Math.Clamp(value, 1, 100);

    public static int UnknownImageTokenReserve(int value) => Math.Clamp(value, 1, 128_000);

    public static int TokenizationPerToolOverheadTokens(int value) => Math.Clamp(value, 0, 128);

    public static int TokenizationProviderFramingTokens(int value) => Math.Clamp(value, 0, 1_024);

    public static int TokenizationStopTokenOverheadTokens(int value) => Math.Clamp(value, 0, 128);

    public static double TokenizationConfidence(double value) =>
        double.IsNaN(value) ? 0d : Math.Clamp(value, 0d, 1d);

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

    public static int EventBusHeartbeatSeconds(int value) => Math.Clamp(value, 0, 300);

    public static int MetadataScanCacheTtlSeconds(int value) => Math.Clamp(value, 0, 300);

    public static int LogRingBufferCapacity(int value) => Math.Clamp(value, 1000, 100_000);

    public static int LogQueryLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static long MaxFileReadSizeBytes(long value) => Math.Clamp(value, 1024, 10 * 1024 * 1024);

    public static long MaxFileWriteSizeBytes(long value) => Math.Clamp(value, 1024, 10 * 1024 * 1024);

    public static long MaxReplaceTextBlockBytes(long value) => Math.Clamp(value, 1024, 4 * 1024 * 1024);

    public static int SessionQueryLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static int SessionStreamReplayLimit(int value) => Math.Clamp(value, 1, 10_000);

    public static int WardTimeoutSeconds(int value) => Math.Clamp(value, 10, 600);

    public static int MaxConcurrentApprentices(int value) => Math.Clamp(value, 1, 50);

    public static int MaxConcurrentApprenticeBranches(int value) => Math.Clamp(value, 1, 64);

    public static int MaxConcurrentA2ATasks(int value) => Math.Clamp(value, 1, 500);

    public static int ChronicleChannelCapacity(int value) => Math.Clamp(value, 100, 10_000);

    public static int SanctumMaxProcessMemoryMb(int value) => Math.Clamp(value, 64, 8192);

    public static int SanctumMaxProcessCount(int value) => Math.Clamp(value, 1, 100);

    public static int SanctumMaxFileWriteMb(int value) => Math.Clamp(value, 1, 1024);

    public static int SanctumProcessTimeoutSeconds(int value) => Math.Clamp(value, 10, 3600);

    public static int SanctumMaxCpuSeconds(int value) => Math.Clamp(value, 0, 3600);

    public static int SanctumMaxMemoryMb(int value) => Math.Clamp(value, 0, 32_768);

    public static int SanctumMaxFileDescriptors(int value) => Math.Clamp(value, 0, 65_536);

    public static int SanctumBreachQueryLimit(int value) => Math.Clamp(value, 1, 1000);

    public static int JsonSchemaMaxDepth(int value) => Math.Clamp(value, 1, 50);

    public static int SanctumMaxBreachCount(int value) => Math.Clamp(value, 100, 100_000);

    public static int MaxActiveWards(int value) => Math.Clamp(value, 1, 500);

    public static int MaxSseConnections(int value) => Math.Clamp(value, 1, 100);

    public static int SseConnectionsPerType(int value) => Math.Clamp(value, 1, 50);

    public static int MaxEntryContentBytes(int value) => Math.Clamp(value, 1024, 16_777_216);

    public static int McpMaxToolsTotalBytes(int value) => Math.Clamp(value, 65_536, 16_777_216);

    public static int McpMaxJsonRpcLineBytes(int value) => Math.Clamp(value, 65_536, 8_388_608);

    public static int McpHttpConnectTimeoutSeconds(int value) => Math.Clamp(value, 1, 300);

    public static int MaxOpenApiMessages(int value) => Math.Clamp(value, 1, 10_000);

    public static int MaxStatelessMessages(int value) => Math.Clamp(value, 1, 10_000);

    public static int MaxContentPartsPerMessage(int value) => Math.Clamp(value, 1, 1_024);

    public static int MaxPingPromptChars(int value) => Math.Clamp(value, 1, 262_144);

    public static int MaxDeclaredTools(int value) => Math.Clamp(value, 0, 256);

    public static int MaxResonantBytes(int value) => Math.Clamp(value, 4096, 1_048_576);

    public static int MaxParameterValueChars(int value) => Math.Clamp(value, 256, 65_536);

    public static int HealthProbeIntervalSeconds(int value) => Math.Clamp(value, 5, 600);

    public static int HealthRecoveryProbeIntervalSeconds(int value) => Math.Clamp(value, 5, 3_600);

    public static int HealthFailureThreshold(int value) => Math.Clamp(value, 1, 100);

    public static int HealthProbeTimeoutSeconds(int value) => Math.Clamp(value, 1, 30);

    public static int EmbeddingsDimensions(int value) => Math.Clamp(value, 64, 4_096);

    public static int EmbeddingsBatchSize(int value) => Math.Clamp(value, 1, 256);

    public static int EmbeddingsChunkSizeChars(int value) => Math.Clamp(value, 128, 8_192);

    public static int EmbeddingsChunkOverlapChars(int value) => Math.Clamp(value, 0, 1_024);

    public static float EmbeddingsSimilarityThreshold(float value) => Math.Clamp(value, 0f, 1f);

    public static int EmbeddingsMaxResults(int value) => Math.Clamp(value, 1, 50);

    public static int EmbeddingsMaxEmbeddingInputChars(int value) => Math.Clamp(value, 1_000, 10_000_000);

    public static int EmbeddingsEmbeddingQueueIntervalSeconds(int value) => Math.Clamp(value, 1, 300);

    public static int EmbeddingsCodebaseMaxFilesToIndex(int value) => Math.Clamp(value, 1, 10_000);

    public static int EmbeddingsCodebaseIndexingIntervalMinutes(int value) => Math.Clamp(value, 5, 1_440);

    public static int EmbeddingsCodebaseWatcherDebounceMilliseconds(int value) => Math.Clamp(value, 50, 5_000);

    public static int EmbeddingsCodebaseMaxWatchers(int value) => Math.Clamp(value, 0, 128);

    public static int EmbeddingsCodebaseReconciliationIntervalMinutes(int value) => Math.Clamp(value, 1, 1_440);

    public static int EmbeddingsCodebaseMaxRetrievedChunks(int value) => Math.Clamp(value, 1, 50);

    public static int EmbeddingsAttachmentChunkSizeCharacters(int value) => Math.Clamp(value, 128, 8_192);

    public static int EmbeddingsAttachmentChunkOverlapCharacters(int value) => Math.Clamp(value, 0, 8_191);

    public static int EmbeddingsAttachmentChunkOverlapForChunkSize(int value, int chunkSize) =>
        Math.Clamp(value, 0, Math.Max(0, chunkSize - 1));

    public static int EmbeddingsAttachmentMaxAttachmentsPerBatch(int value) => Math.Clamp(value, 1, 100);

    public static int EmbeddingsAttachmentQueueCapacity(int value) => Math.Clamp(value, 1, 10_000);

    public static int EmbeddingsAttachmentMaxRetrievedChunks(int value) => Math.Clamp(value, 1, 50);

    public static int EmbeddingsAttachmentMaxRetrievedAttachments(int value) => Math.Clamp(value, 1, 100);

    public static int EmbeddingsAttachmentMaxRetrievedBytes(int value) => Math.Clamp(value, 1_024, 16 * 1024 * 1024);

    public static int EmbeddingsAttachmentMaxRetrievedTokens(int value) => Math.Clamp(value, 128, 1024 * 1024);

    public static int EmbeddingsSpellRoutingHybridTopK(int value) => Math.Clamp(value, 1, 20);

    public static int EmbeddingsTapestryMaxTreeDepth(int value) => Math.Clamp(value, 1, 16);

    public static int EmbeddingsTapestryTargetChildrenPerSummary(int value) => Math.Clamp(value, 2, 64);

    public static int EmbeddingsTapestryMaxChildrenPerSummary(int value) => Math.Clamp(value, 2, 256);

    public static int EmbeddingsTapestryMaxClustersPerLayer(int value) => Math.Clamp(value, 2, 4_096);

    public static int EmbeddingsTapestryMaxSummaryTokens(int value) => Math.Clamp(value, 64, 8_192);

    public static int EmbeddingsTapestryRebuildIntervalMinutes(int value) => Math.Clamp(value, 1, 1_440);

    public static int EmbeddingsTapestryMaxRetrievedNodes(int value) => Math.Clamp(value, 1, 50);

    public static int EmbeddingsTapestryMaxRetrievedBytes(int value) => Math.Clamp(value, 1_024, 16 * 1024 * 1024);

    public static int EmbeddingsTapestryMaxRetrievedTokens(int value) => Math.Clamp(value, 128, 1024 * 1024);

    public static long ScryingMaxImageBytes(long value) => Math.Clamp(value, 1024L, 20L * 1024L * 1024L);

    public static int ScryingMaxImagesPerRequest(int value) => Math.Clamp(value, 1, 100);

    public static int HostAuditLogMaxSizeMb(int value) => Math.Clamp(value, 10, 1_000);

    public static double PricingInputPer1M(double value) => Math.Clamp(value, 0.0, 1_000_000.0);

    public static double PricingOutputPer1M(double value) => Math.Clamp(value, 0.0, 1_000_000.0);

    public static decimal PricingRatePer1M(decimal value) => Math.Clamp(value, 0m, 1_000_000m);

    public static decimal BudgetDailyLimitUsd(decimal value) => Math.Clamp(value, 0.00m, 1_000_000.00m);

    public static int BudgetAlertThresholdPercent(int value) => Math.Clamp(value, 1, 100);

    public static int WebBrowsingMaxContentBytes(int value) => Math.Clamp(value, 1_000, 1_000_000);

    public static int WebBrowsingIdleTimeoutSeconds(int value) => Math.Clamp(value, 1, 300);

    public static int WebBrowsingMaxLinks(int value) => Math.Clamp(value, 0, 100);

    public static int WebBrowsingMaxQueryChars(int value) => Math.Clamp(value, 1, 20_000);

    public static int WebBrowsingMaxUrlChars(int value) => Math.Clamp(value, 1, 16_384);

    public static int WebBrowsingMaxResponseBytes(int value) =>
        Math.Clamp(value, 1_000, 10_000_000);

    public static int WebBrowsingMaxLinkUrlChars(int value) => Math.Clamp(value, 128, 16_384);

    public static int WebBrowsingMaxCitations(int value) => Math.Clamp(value, 0, 100);

    public static int WebBrowsingMaxCitationUrlChars(int value) => Math.Clamp(value, 128, 16_384);

    public static int WebBrowsingMaxRedirects(int value) => Math.Clamp(value, 0, 20);

    public static int ClientToolForwardingMaxClientTools(int value) => Math.Clamp(value, 1, 100);

    /// <summary>
    /// Streaming output-filter mode for guardrails. Enum values only; unknown strings are not
    /// accepted by binding — callers that still hold a legacy string should map explicitly.
    /// </summary>
    public static GuardrailsStreamingMode GuardrailsStreamingMode(GuardrailsStreamingMode value) => value;

}
