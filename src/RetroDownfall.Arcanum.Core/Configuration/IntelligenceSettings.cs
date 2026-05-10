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

}



