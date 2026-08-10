namespace RetroDownfall.Arcanum.Core.TheForge;

public sealed record ApprenticeCheckpoint
{

    public int CurrentStep { get; init; }

    public string? ConversationSummary { get; init; }

    private readonly List<string> _completedToolCallIds = [];

    public IReadOnlyList<string> CompletedToolCallIds
    {

        // Return a non-downcastable read-only view so a consumer cannot cast back to List<string>
        // and mutate a checkpoint that is frozen once persisted.
        get => _completedToolCallIds.AsReadOnly();

        // Tolerate null: because every member is init-only the source generator constructs through
        // ObjectWithParameterizedConstructorCreator and feeds the parameter default (null) in when
        // the JSON member is absent or explicitly null, which would otherwise throw out of a
        // CheckpointData read.
        init => _completedToolCallIds = value is null ? [] : new List<string>(value);

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
