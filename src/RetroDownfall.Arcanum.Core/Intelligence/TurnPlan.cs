namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>A structured in-turn plan/task model for agentic turn planning.</summary>
public sealed record TurnPlanTask(
    string TaskId,
    string Description,
    TurnPlanTaskStatus Status,
    string? DependsOn,
    int Ordinal,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public enum TurnPlanTaskStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Failed = 3,
    Skipped = 4,
}

public sealed record TurnPlan(
    string PlanId,
    string Title,
    IReadOnlyList<TurnPlanTask> Tasks,
    DateTimeOffset CreatedAt,
    TurnPlanStatus Status);

public enum TurnPlanStatus
{
    Draft = 0,
    Active = 1,
    Completed = 2,
    Abandoned = 3,
}
