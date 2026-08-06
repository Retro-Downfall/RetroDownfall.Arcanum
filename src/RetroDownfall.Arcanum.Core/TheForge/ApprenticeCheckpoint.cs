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

    /// <summary>
    /// A2A delegation hops that led to this Apprentice, oldest first, ending with this instance's own node
    /// id. Empty/absent for purely local work. Kept here (rather than in a new column) for the same reason
    /// as <see cref="ParentApprenticeId"/>: no schema change, no compiled-model regeneration.
    /// </summary>
    public IReadOnlyList<string>? DelegationChain { get; init; }

}
