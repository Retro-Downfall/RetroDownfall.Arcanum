namespace RetroDownfall.Arcanum.Core.Sanctum;

public sealed record SanctumBreach
{

    public string BreachId { get; init; } = "";

    public string CampaignId { get; init; } = "";

    public string ToolName { get; init; } = "";

    public string BreachType { get; init; } = "";

    public string Detail { get; init; } = "";

    public string? RequestedPath { get; init; }

    public string? ResolvedPath { get; init; }

    public string? WorkspaceRoot { get; init; }

    public string? RequestedUrl { get; init; }

    public DateTimeOffset Timestamp { get; init; }

}
