namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record CreateApprenticeRequest(
    string Name,
    string Goal,
    Guid? CampaignId = null,
    string? WorkspacePath = null);

public sealed record ReweaveApprenticeRequest(IReadOnlyList<PlanStep> Steps);

public sealed record InterveneApprenticeRequest(string Guidance, bool Resume = true);

public sealed record CastApprenticeRequest(string Goal, string? Name = null);

public sealed record ApprenticeSummaryDto(
    Guid Id,
    Guid? CampaignId,
    string Name,
    string Goal,
    string Status,
    int CurrentStep,
    int PlanStepCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApprenticeDetailDto(
    Guid Id,
    Guid? CampaignId,
    Guid? ParentApprenticeId,
    string Name,
    string Goal,
    IReadOnlyList<PlanStep> Plan,
    int CurrentStep,
    string Status,
    Guid? SessionId,
    string WorkspacePath,
    ApprenticeCheckpoint? Checkpoint,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
