namespace RetroDownfall.Arcanum.Core.Configuration;

/// <summary>
/// Code-owned runtime projection for turn mechanics. This record is not a public configuration root.
/// </summary>
public sealed record IntelligenceSettings
{

    public int ExecuteCommandTimeoutSeconds { get; set; } = 30;

    public int SemanticRouterPreflightTimeoutSeconds { get; set; } = 15;

    public int SemanticRouterMaxTokens { get; set; } = 128;

    public float SemanticRouterTemperature { get; set; } = 0.0f;

    public int ListDirectoryMaxPaths { get; set; } = 500;

    public bool EnableLexiconSystem { get; set; } = true;

    /// <summary>Maximum Lexicon entries returned per inference-turn <c>MatchEntitiesAsync</c> query.</summary>
    public int LexiconMaxMatchedEntries { get; set; } = 16;

    /// <summary>Hard cap (bytes) on the rendered <c>### Lexicon (Known Context)</c> DATA block.</summary>
    public int LexiconMaxInjectedBytes { get; set; } = 4096;

    public bool EnableArchiveSearch { get; set; } = true;

    public int ArchiveSearchMaxResults { get; set; } = 5;

    public int ArchiveSearchMaxQueryLength { get; set; } = 512;

    public int CampaignLogThreshold { get; set; } = 25;

    public int CampaignLogIdleTimeoutMinutes { get; set; } = 240;

    public int CampaignLogSweepIntervalMinutes { get; set; } = 15;

    public int ContextWindowCompressionThreshold { get; set; } = 85;

    public bool EnableContextCompression { get; set; } = true;

    public bool EnableTokenTracking { get; set; } = true;

    /// <summary>
    /// Hard cap (bytes) on captured <c>stdout</c> and <c>stderr</c> for in-process MCP
    /// <c>execute_command</c> and the <c>run_spell_script</c> hub tool. Output beyond this is
    /// truncated with a marker so verbose tool calls cannot exhaust host memory.
    /// </summary>
    public long ToolOutputCapBytes { get; set; } = 1L * 1024L * 1024L;

    /// <summary>
    /// Internal mode switch. When <see langword="true"/>, an unexpected exception from a single tool
    /// invocation during a <em>buffered</em> (<c>/api/intelligence/ping</c>, forge execute)
    /// inference turn is caught and synthesized into a tool result
    /// (<c>ToolExecutionPipeline.PublicToolFailureMessage</c>) so the model can see the failure and
    /// decide how to proceed, instead of the whole turn failing with <c>Hub.Error</c>. Only
    /// unexpected (infrastructure-fault) exceptions are affected — expected tool errors (validation,
    /// ward/Sanctum denial) already return a structured tool result regardless of this switch. The
    /// retained public projection fixes the tolerant policy on both buffered and streaming paths.
    /// </summary>
    public bool TolerateToolFailures { get; set; } = true;

    /// <summary>
    /// Minimum history-message count before the explicit/manual compact operation tokenizes and
    /// prunes entries. Live provider-call admission always measures the materialized context.
    /// </summary>
    public int CompressionPreflightMinMessages { get; set; } = 6;

    /// <summary>
    /// Default per-message framing for calibrated/unknown tokenization profiles. A provider/model
    /// profile may override it. Default 4.
    /// </summary>
    public int PerMessageTemplateOverheadTokens { get; set; } = 4;

    /// <summary>
    /// Linker-safe fallback tokenizer for calibrated and unknown model profiles. Known built-in
    /// exact profiles resolve their own tokenizer id.
    /// </summary>
    public string TokenizerEncoding { get; set; } = "o200k_base";

    /// <summary>
    /// Safety margin added to calibrated and unknown input-token estimates. Exact tokenizer output
    /// is never inflated. Default 15 percent.
    /// </summary>
    public int EstimatedTokenSafetyMarginPercent { get; set; } = 15;

    /// <summary>
    /// Conservative per-image reserve when no provider/model-specific image formula can be applied.
    /// Such image costs remain classified as estimated, never exact. Default 2048 tokens.
    /// </summary>
    public int UnknownImageTokenReserve { get; set; } = 2048;

    public int MaxOpenApiMessages { get; set; } = 1_000;

    public int MaxStatelessMessages { get; set; } = 100;

    public int MaxContentPartsPerMessage { get; set; } = 64;

    public int MaxPingPromptChars { get; set; } = 32_768;

    /// <summary>
    /// Code-owned client-disconnect policy for streaming inference. <see cref="DisconnectPolicy.Auto"/>
    /// continues then replays when an <c>Idempotency-Key</c> is present; otherwise it cancels and
    /// abandons the claim.
    /// </summary>
    public DisconnectPolicy DisconnectPolicy { get; set; } = DisconnectPolicy.Auto;

    /// <summary>
    /// Tokens reserved for model output when running per-call context preflight.
    /// Used when the request does not specify <c>MaxOutputTokens</c>. Default 1024.
    /// </summary>
    public int ReservedOutputTokens { get; set; } = 1024;

    /// <summary>
    /// When <c>true</c>, semantic spell-router preflight uses <see cref="ArcanumSettings.FastModel"/> when configured.
    /// </summary>
    public bool UseFastModelForSpellRouting { get; set; }

}



