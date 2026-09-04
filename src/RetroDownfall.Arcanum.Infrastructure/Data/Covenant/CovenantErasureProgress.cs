using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

/// <summary>
/// What one run of an erasure has actually done so far, as opposed to what it set out to do.
/// </summary>
/// <remarks>
/// Mutable and passed by reference, which is deliberate. Most of what this records is decided on paths
/// that go on to fail, and the failure still has to be answered with what had already happened —
/// whether admission may reopen turns entirely on it. A shape that carried these back in a success
/// value would drop them on exactly the paths that need them.
///
/// <para>Seeded from the phase a run resumed at rather than from nothing, because a resumed run
/// inherits every irreversible act the runs before it performed. A fresh record would let a resumed
/// run abort as though the installation were untouched.</para>
/// </remarks>
internal sealed class CovenantErasureProgress(CovenantResetPhase resumedFrom)
{

    /// <summary>Whether a previous pass already committed the canonical erasure.</summary>
    internal bool CanonicalResetApplied { get; set; } =
        resumedFrom >= CovenantResetPhase.CanonicalApplied;

    internal bool LocalSecureErasureComplete { get; set; }

    /// <summary>Whether control has crossed from inventory into any protected effect.</summary>
    internal bool EffectAttempted { get; set; }

    internal CovenantDisclosureExposure Exposure { get; set; } =
        new(0, CovenantDisclosureCountKind.Exact);

    /// <summary>
    /// Whether anything irreversible has happened yet, which is the only fact that separates a
    /// reopening abort from one that must keep admission closed.
    /// </summary>
    internal bool DurablyMutated { get; set; } = resumedFrom > CovenantResetPhaseMachine.First;

}
