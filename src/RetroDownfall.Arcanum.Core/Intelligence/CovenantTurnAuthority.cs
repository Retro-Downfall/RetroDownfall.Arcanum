using RetroDownfall.Arcanum.Core.Covenant;

namespace RetroDownfall.Arcanum.Core.Intelligence;

/// <summary>
/// What one live turn is permitted to do with protected content, and the lease that permits it.
/// </summary>
/// <remarks>
/// A closed three-case union: the base constructor is private, so no other assembly and no other type
/// in this one can add a fourth case. That closure is what lets a commit path exhaustively decide
/// whether a turn may disclose Covenant content without a default arm that has to guess (§10.12).
///
/// <para>The distinction between <see cref="ForProtectedHistory"/> and
/// <see cref="ForCurrentCovenant"/> is not cosmetic. A turn that merely slices tainted history holds a
/// lease so reset and erasure can drain it, but it has no current Covenant plan and must not invent
/// one. Only a turn that actually admitted Covenant content this time may stage a mutation or append a
/// Covenant disclosure receipt.</para>
///
/// <para>Nonserializable by construction: the lease it carries is a live capability, and a value that
/// could be written to a wire and read back would be a capability anyone could mint.</para>
/// </remarks>
public abstract class CovenantTurnAuthority
{

    private CovenantTurnAuthority()
    {
    }

    /// <summary>A turn with no lease, no protected reach, and no Covenant effect of any kind.</summary>
    public static CovenantTurnAuthority Unprotected { get; } = new UnprotectedTurn();

    /// <summary>The live lease this authority holds, or <see langword="null"/> when unprotected.</summary>
    public abstract CovenantTurnLease? Lease { get; }

    /// <summary>Whether this turn may read Covenant-derived history.</summary>
    public abstract bool CanReadProtectedHistory { get; }

    /// <summary>Whether this turn admitted current Covenant content into its plan.</summary>
    public abstract bool CanAdmitCovenantContent { get; }

    /// <summary>Whether this turn may stage and publish Covenant mutations.</summary>
    public abstract bool CanStageCovenantMutation { get; }

    /// <summary>
    /// A turn that reads tainted history but admits no current Covenant content.
    /// </summary>
    public static CovenantTurnAuthority ForProtectedHistory(CovenantTurnLease lease)
    {

        ArgumentNullException.ThrowIfNull(lease);

        return new ProtectedHistoryTurn(lease);

    }

    /// <summary>
    /// A turn that admitted current Covenant content and may therefore stage mutations.
    /// </summary>
    public static CovenantTurnAuthority ForCurrentCovenant(CovenantTurnLease lease)
    {

        ArgumentNullException.ThrowIfNull(lease);

        return new CurrentCovenantTurn(lease);

    }

    private sealed class UnprotectedTurn : CovenantTurnAuthority
    {

        public override CovenantTurnLease? Lease => null;

        public override bool CanReadProtectedHistory => false;

        public override bool CanAdmitCovenantContent => false;

        public override bool CanStageCovenantMutation => false;

    }

    private sealed class ProtectedHistoryTurn(CovenantTurnLease lease) : CovenantTurnAuthority
    {

        public override CovenantTurnLease? Lease { get; } = lease;

        public override bool CanReadProtectedHistory => true;

        public override bool CanAdmitCovenantContent => false;

        public override bool CanStageCovenantMutation => false;

    }

    private sealed class CurrentCovenantTurn(CovenantTurnLease lease) : CovenantTurnAuthority
    {

        public override CovenantTurnLease? Lease { get; } = lease;

        public override bool CanReadProtectedHistory => true;

        public override bool CanAdmitCovenantContent => true;

        public override bool CanStageCovenantMutation => true;

    }

}
