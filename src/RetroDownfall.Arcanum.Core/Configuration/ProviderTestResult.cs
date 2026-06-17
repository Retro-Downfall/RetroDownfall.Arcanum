namespace RetroDownfall.Arcanum.Core.Configuration;

public sealed record ProviderTestResult(
    bool IsReachable,
    long LatencyMs,
    string[] ModelsFound,
    string? Error);
