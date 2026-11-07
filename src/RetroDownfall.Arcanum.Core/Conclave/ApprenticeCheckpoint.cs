using RetroDownfall.Arcanum.Core.Guild;

namespace RetroDownfall.Arcanum.Core.Conclave;

public sealed record ApprenticeCheckpoint
{
    public int CurrentStep { get; init; }

    public string? ConversationSummary { get; init; }

    private readonly List<string> _completedToolCallIds = [];

    /// <summary>
    /// Carried forward on every checkpoint rewrite, but no execution path records into it: recovery
    /// resumes from the persisted step position and delegation lineage, and an interrupted step is
    /// re-run rather than replayed past the tool calls it already made.
    /// </summary>
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

    /// <summary>Host-stamped goal attribution. Missing historical evidence remains unknown.</summary>
    public GuildAuthorityKind GoalAuthority { get; init; } = GuildAuthorityKind.LegacyUnknown;

    /// <summary>Guidance admitted by the authenticated native operator boundary.</summary>
    public string? OperatorGuidance { get; init; }

    /// <summary>Reserved for a verified issuing-parent boundary; never populated by legacy A2A.</summary>
    public string? ParentGuidance { get; init; }

    /// <summary>Legacy or unverified guidance. Its historical name conveys no operator authority.</summary>
    public string? DmGuidance { get; init; }

    public Guid? ParentApprenticeId { get; init; }

    /// <summary>
    /// A2A delegation hops that led to this Apprentice, oldest first, ending with this instance's own node
    /// id. Empty/absent for purely local work. Kept here (rather than in a new column) for the same reason
    /// as <see cref="ParentApprenticeId"/>: no schema change, no compiled-model regeneration.
    /// </summary>
    public IReadOnlyList<string>? DelegationChain { get; init; }

    /// <summary>
    /// True when the creating operation durably requested execution. An Idle Apprentice carrying this
    /// intent is restarted after a process crash; manually minted Idle children remain idle.
    /// </summary>
    public bool LaunchRequested { get; init; }
}
