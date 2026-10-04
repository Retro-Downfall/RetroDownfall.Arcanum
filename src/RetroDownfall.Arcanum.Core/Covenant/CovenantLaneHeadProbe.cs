namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What one exact scoped key currently holds in one lane.
/// </summary>
/// <remarks>
/// The three states are distinct because a mutation has to treat them differently: an
/// <see cref="Absent"/> key is a create, a <see cref="Present"/> one is an update against an exact
/// revision, and a <see cref="Retired"/> one is a reactivation the operator has to ask for
/// explicitly. Collapsing retired into absent is how a tombstone silently becomes a resurrection.
/// </remarks>
public enum CovenantLaneHeadPresence : byte
{

    Absent = 1,

    Present = 2,

    Retired = 3,

}

/// <summary>
/// What erasure evidence says about an agent authoring one exact scoped key.
/// </summary>
/// <remarks>
/// In-process only: it rides on the staging probes so a handler can refuse before it stages, and it is
/// never serialized. <see cref="Withheld"/> is answered exactly as a pin is, so an agent cannot tell an
/// erased key from a pinned one. The two key states are store-level facts and carry no identity: a
/// store whose evidence cannot be verified withholds every agent write, whatever the key.
/// </remarks>
public enum CovenantAgentErasureState : byte
{

    /// <summary>No evidence names this identity, or the store holds no Covenant evidence at all.</summary>
    Clear = 1,

    /// <summary>The operator erased this exact identity in this exact scope.</summary>
    Withheld = 2,

    /// <summary>The store holds Covenant evidence and the latched key is not in hand to check it.</summary>
    KeyUnavailable = 3,

    /// <summary>
    /// The store holds Covenant evidence that no present key can verify: the key is absent, or the
    /// evidence was recorded under another one.
    /// </summary>
    KeyLost = 4,

}

/// <summary>
/// One bounded, index-only answer about a scoped lane head.
/// </summary>
/// <remarks>
/// Carries no authored or compiled content. This is the probe a preflight and a mutation run before
/// deciding what they are about to do, and neither needs the text to decide it.
/// </remarks>
public sealed record CovenantLaneHeadProbe(
    CovenantOperationScope Scope,
    CovenantLane Lane,
    string NormalizedKey,
    CovenantLaneHeadPresence Presence,
    Guid? EntryId,
    Guid? VersionId,
    long LaneRevision,
    CovenantOrigin? Origin,
    long CompiledByteCost,
    long KeyEpoch,

    /// <summary>Whether the operator has pinned this scoped lane against agent authorship.</summary>
    /// <remarks>
    /// Reported so a staging handler can refuse before it stages. The write authority inside the
    /// publication transaction is what actually enforces the pin — this is the early answer that keeps
    /// a refused proposal from costing the turn the answer it was carrying.
    /// </remarks>
    bool IsPinned = false,

    /// <summary>What erasure evidence says about an agent authoring this scoped key.</summary>
    /// <remarks>
    /// Classified inside the probe's own read, from the latched key alone, for the same early refusal
    /// the pin gets. The write authority classifies again inside the publication transaction.
    /// </remarks>
    CovenantAgentErasureState AgentErasure = CovenantAgentErasureState.Clear)
{

    /// <summary>Whether the operator erased this exact scoped key, so an agent may not author it.</summary>
    public bool IsAgentWithheld => AgentErasure == CovenantAgentErasureState.Withheld;

    /// <summary>
    /// The absent answer for a key that has no row in this scope and lane at all.
    /// </summary>
    public static CovenantLaneHeadProbe NotFound(
        CovenantOperationScope scope,
        CovenantLane lane,
        string normalizedKey,
        long keyEpoch) =>
        new(
            scope,
            lane,
            normalizedKey,
            CovenantLaneHeadPresence.Absent,
            EntryId: null,
            VersionId: null,
            LaneRevision: 0,
            Origin: null,
            CompiledByteCost: 0,
            keyEpoch);

}
