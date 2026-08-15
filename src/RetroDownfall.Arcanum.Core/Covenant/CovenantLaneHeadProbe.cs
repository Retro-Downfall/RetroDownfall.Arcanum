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
    long KeyEpoch)
{

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
