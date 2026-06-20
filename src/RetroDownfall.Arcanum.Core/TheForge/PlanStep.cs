namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record PlanStep
{

    public int Index { get; init; }

    public string Description { get; init; } = string.Empty;

    public string Status { get; init; } = "pending";

    public string? Result { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int Attempts { get; init; }

    public bool IsParallel { get; init; }

}
