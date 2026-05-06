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

}


