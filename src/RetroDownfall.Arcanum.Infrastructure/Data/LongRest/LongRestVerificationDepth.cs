namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>How much of a generation's frozen corpus one verification re-proves.</summary>
/// <remarks>
/// Verify the whole at the seams and the parts at the steps. A caller always names its depth: nothing here has a default, so a new gate
/// cannot inherit the cheap one by omission.
/// </remarks>
internal enum LongRestVerificationDepth
{
    /// <summary>
    /// A unit of work, each time it asks whether the generation still holds. Never a corpus pass: the generation row, the fence, the set-based
    /// metadata question over every leaf, the exact inputs the step uses, and one rotating window of
    /// <see cref="LongRestDiscoveryStore.ScrubWindowLeaves"/> leaves re-proved in full.
    /// </summary>
    Step,

    /// <summary>
    /// A point where the sweep's footing can have changed without a trigger seeing it, or where one verdict closes a stage: publish,
    /// completion, readmission and resume entry, capability issuance, interrupted settlement and completed-replay adoption. Every leaf.
    /// </summary>
    Seam,
}
