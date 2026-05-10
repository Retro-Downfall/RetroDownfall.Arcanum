namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record IntelligenceSettings
{

    public int ExecuteCommandTimeoutSeconds { get; init; } = 30;

    public int SemanticRouterPreflightTimeoutSeconds { get; init; } = 15;

    public int SemanticRouterMaxTokens { get; init; } = 50;

    public float SemanticRouterTemperature { get; init; } = 0.0f;

    public int McpRequestTimeoutSeconds { get; init; } = 60;

    public int McpMaxPaginationPages { get; init; } = 32;

    public int ListDirectoryMaxPaths { get; init; } = 500;

    public bool EnableLoreSystem { get; init; } = true;

    public bool EnableArchiveSearch { get; init; } = true;

    public int ArchiveSearchMaxResults { get; init; } = 5;

    public int ArchiveSearchMaxQueryLength { get; init; } = 512;

    public int CampaignLogThreshold { get; init; } = 25;

    public int CampaignLogIdleTimeoutMinutes { get; init; } = 240;

    public int CampaignLogSweepIntervalMinutes { get; init; } = 15;

    public int ContextWindowCompressionThreshold { get; init; } = 85;

    public bool EnableContextCompression { get; init; } = true;

    public bool EnableTokenTracking { get; init; } = true;

    /// <summary>
    /// Hard cap (bytes) on captured <c>stdout</c> and <c>stderr</c> for in-process MCP
    /// <c>execute_command</c> and the <c>run_spell_script</c> hub tool. Output beyond this is
    /// truncated with a marker so verbose tool calls cannot exhaust host memory.
    /// </summary>
    public long ToolOutputCapBytes { get; init; } = 1L * 1024L * 1024L;

    /// <summary>
    /// Maximum number of agentic tool rounds the hub will execute per inference turn.
    /// A round = one model response containing tool calls + one server-side execution batch.
    /// Beyond this cap, the hub fails the turn with <c>Hub.ToolLoop</c>.
    /// </summary>
    public int MaxToolInferenceRounds { get; init; } = 8;

    /// <summary>
    /// Minimum assembled-message count before context-compression preflight runs. Short threads
    /// are assumed to fit and skip tokenizer cost. Default 6.
    /// </summary>
    public int CompressionPreflightMinMessages { get; init; } = 6;

    /// <summary>
    /// Per-message overhead (tokens) added to the pre-flight count to approximate chat-template
    /// framing (role markers, separators). Default 4.
    /// </summary>
    public int PerMessageTemplateOverheadTokens { get; init; } = 4;

    /// <summary>
    /// Tiktoken encoding name used by <c>InferenceTokenizerResolver</c>. Default <c>o200k_base</c>.
    /// Operators only need to change this if they validate counts against a specific
    /// non-OpenAI model family that ships a different encoding.
    /// </summary>
    public string TokenizerEncoding { get; init; } = "o200k_base";

}



