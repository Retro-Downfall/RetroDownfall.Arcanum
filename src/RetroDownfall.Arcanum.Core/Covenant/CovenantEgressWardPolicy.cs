using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What one Covenant tool call needs before it is allowed to have an effect.
/// </summary>
public enum CovenantEgressAuthorization : byte
{

    /// <summary>Not a Covenant mutation; this policy has nothing to say about it.</summary>
    NotSensitive = 1,

    /// <summary>
    /// A proposal: its arguments stay private, but it needs no additional authorization. It writes
    /// only the Proposed lane, which is review-only beside effective Confirmed content.
    /// </summary>
    SensitivePayloadOnly = 2,

    /// <summary>
    /// A retirement authorized by the live invocation and its exact canonical preflight.
    /// </summary>
    UngatedRetirement = 3,

    /// <summary>This invocation may not stage a Covenant mutation at all.</summary>
    DeniedIneligibleTurn = 5,

}

/// <summary>
/// The resolved authorization one classified tool call runs under.
/// </summary>
public sealed record CovenantEgressWardDecision(
    CovenantEgressAuthorization Authorization,
    CovenantToolRiskIdentity RiskIdentity)
{

    public bool IsDenied =>
        Authorization is CovenantEgressAuthorization.DeniedIneligibleTurn;

}

/// <summary>
/// The live authorization policy for Covenant sensitive egress.
/// </summary>
/// <remarks>
/// Feature and canonical health, invocation authority, and the one-call capability authorize an
/// advertised retirement. The host records the ordinary Ungated Ward audit pair and does not ask
/// again. Resolution still runs against the live invocation so revoked authority, a missing Campaign
/// binding, or a disabled tool policy refuses the call before any effect.
/// </remarks>
public static class CovenantEgressWardPolicy
{

    public static CovenantEgressWardDecision Resolve(
        ProviderToolCallClassification classification,
        ArcanumInvocationContext invocation)
    {

        ArgumentNullException.ThrowIfNull(classification);

        ArgumentNullException.ThrowIfNull(invocation);

        if (!classification.IsCovenantMutation)
        {
            return Decision(
                CovenantEgressAuthorization.NotSensitive,
                classification.RiskIdentity);
        }

        bool retirement = string.Equals(
            classification.ToolName,
            CovenantToolNames.RetireCovenant,
            StringComparison.Ordinal);

        bool eligible = retirement
            ? invocation.CanPrepareCovenantRetirement
            : invocation.CanStageCovenantMutation;

        if (!eligible)
        {
            return Decision(CovenantEgressAuthorization.DeniedIneligibleTurn);
        }

        return retirement
            ? Decision(CovenantEgressAuthorization.UngatedRetirement)
            : Decision(CovenantEgressAuthorization.SensitivePayloadOnly);

    }

    private static CovenantEgressWardDecision Decision(
        CovenantEgressAuthorization authorization,
        CovenantToolRiskIdentity riskIdentity = CovenantToolRiskIdentity.CovenantSensitiveEgress) =>
        new(authorization, riskIdentity);

}
