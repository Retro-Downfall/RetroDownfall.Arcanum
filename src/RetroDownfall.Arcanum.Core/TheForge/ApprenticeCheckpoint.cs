namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record ApprenticeCheckpoint
{

    public int CurrentStep { get; init; }

    public string? ConversationSummary { get; init; }

    private readonly List<string> _completedToolCallIds = [];

    public IReadOnlyList<string> CompletedToolCallIds
    {

        get => _completedToolCallIds;

        init => _completedToolCallIds = new List<string>(value);

    }

    public DateTimeOffset Timestamp { get; init; }

    public string? EscalationReason { get; init; }

    public string? DmGuidance { get; init; }

    public Guid? ParentApprenticeId { get; init; }

}
