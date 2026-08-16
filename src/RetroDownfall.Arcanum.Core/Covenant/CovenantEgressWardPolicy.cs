using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>
/// What one Covenant tool call needs before it is allowed to have an effect.
/// </summary>
public enum CovenantEgressAuthorization : byte
{

    /// <summary>Not a Covenant mutation; this policy has nothing to say about it.</summary>
    NotSensitive = 1,

    /// <summary>
    /// A proposal: its arguments stay private, but it needs no Ward. It writes only the Proposed
    /// lane, which is review-only beside effective Confirmed content and cannot change it.
    /// </summary>
    SensitivePayloadOnly = 2,

    /// <summary>A retirement: an operator has to see the exact target and approve it.</summary>
    AttendedWardRequired = 3,

    /// <summary>The operator pre-authorized this exact tool through the auto-approval allowlist.</summary>
    ConfiguredAutoApproval = 4,

    /// <summary>This invocation may not stage a Covenant mutation at all.</summary>
    DeniedIneligibleTurn = 5,

    /// <summary>Wards are switched off, so there is no way for the operator to be asked.</summary>
    DeniedWardsDisabled = 6,

}

/// <summary>
/// The resolved authorization one classified tool call runs under.
/// </summary>
public sealed record CovenantEgressWardDecision(
    CovenantEgressAuthorization Authorization,
    CovenantToolRiskIdentity RiskIdentity,
    CovenantAuthorizationMode Mode)
{

    public bool IsDenied =>
        Authorization is CovenantEgressAuthorization.DeniedIneligibleTurn
            or CovenantEgressAuthorization.DeniedWardsDisabled;

    /// <summary>Whether a live operator prompt has to be raised and awaited.</summary>
    public bool RequiresOperatorPrompt =>
        Authorization is CovenantEgressAuthorization.AttendedWardRequired;

    /// <summary>Whether consent — interactive or configured — is part of this call's evidence.</summary>
    public bool CarriesWardEvidence =>
        Authorization is CovenantEgressAuthorization.AttendedWardRequired
            or CovenantEgressAuthorization.ConfiguredAutoApproval;

}

/// <summary>
/// The dynamic Ward policy for Covenant sensitive egress.
/// </summary>
/// <remarks>
/// Resolution is deliberately re-run against the <em>live</em> invocation rather than a value
/// captured when the turn began. Between the plan and the tool call the context policy can be
/// revoked, authority can be tainted, and the Campaign binding can change, and each of those makes an
/// earlier "yes" wrong.
///
/// <para>One rule here diverges from <see cref="ToolRiskClassifier.RequiresWard"/> on purpose: with
/// Wards disabled an ordinary Forbidden Art executes unwarded, but Covenant retirement is denied.
/// Switching Wards off removes the operator's only opportunity to refuse, and an agent erasing the
/// operator's own standing instructions is not something silence should authorize (§10.14).</para>
/// </remarks>
public static class CovenantEgressWardPolicy
{

    public static CovenantEgressWardDecision Resolve(
        ProviderToolCallClassification classification,
        ArcanumInvocationContext invocation,
        WardSettings wardSettings)
    {

        ArgumentNullException.ThrowIfNull(classification);

        ArgumentNullException.ThrowIfNull(invocation);

        ArgumentNullException.ThrowIfNull(wardSettings);

        if (!classification.IsCovenantMutation)
        {
            return new CovenantEgressWardDecision(
                CovenantEgressAuthorization.NotSensitive,
                classification.RiskIdentity,
                CovenantAuthorizationMode.None);
        }

        if (!invocation.CanStageCovenantMutation)
        {
            return Denied(CovenantEgressAuthorization.DeniedIneligibleTurn);
        }

        if (!string.Equals(classification.ToolName, CovenantToolNames.RetireCovenant, StringComparison.Ordinal))
        {
            return new CovenantEgressWardDecision(
                CovenantEgressAuthorization.SensitivePayloadOnly,
                CovenantToolRiskIdentity.CovenantSensitiveEgress,
                CovenantAuthorizationMode.None);
        }

        if (!wardSettings.Enabled)
        {
            return Denied(CovenantEgressAuthorization.DeniedWardsDisabled);
        }

        return ToolRiskClassifier.IsAutoApproved(classification.ToolName, wardSettings)
            ? new CovenantEgressWardDecision(
                CovenantEgressAuthorization.ConfiguredAutoApproval,
                CovenantToolRiskIdentity.CovenantSensitiveEgress,
                CovenantAuthorizationMode.WardConfiguredAutoApproval)
            : new CovenantEgressWardDecision(
                CovenantEgressAuthorization.AttendedWardRequired,
                CovenantToolRiskIdentity.CovenantSensitiveEgress,
                CovenantAuthorizationMode.WardInteractive);

    }

    /// <summary>
    /// Turns a resolved decision plus the operator's answer into evidence bound to that exact call.
    /// </summary>
    /// <remarks>
    /// A receipt exists only where a Ward was actually placed. Manufacturing one for an unwarded or
    /// policy-denied call would produce evidence of consent nobody gave.
    /// </remarks>
    public static Result<CovenantToolWardReceipt> Accept(
        CovenantEgressWardDecision decision,
        ProviderToolCallClassification classification,
        CovenantWardDecision operatorDecision,
        ProviderCallSensitivity sensitivity,
        CovenantEgressDestination destination,
        CovenantDigest opaqueDestinationIdentityDigest,
        ulong operatorAuthorityEpoch)
    {

        ArgumentNullException.ThrowIfNull(decision);

        ArgumentNullException.ThrowIfNull(classification);

        ArgumentNullException.ThrowIfNull(sensitivity);

        if (!decision.CarriesWardEvidence)
        {
            return Result<CovenantToolWardReceipt>.Failure(new Error(
                ErrorCodes.Covenant.ForbiddenAuthority,
                "This Covenant tool call placed no Ward, so it has no operator consent to record."));
        }

        return Result<CovenantToolWardReceipt>.Success(new CovenantToolWardReceipt(
            new WardEvidenceDigestInput(
                classification.FrozenNameDigest,
                classification.CanonicalArgumentDigest,
                decision.RiskIdentity,
                sensitivity.Digest,
                destination,
                opaqueDestinationIdentityDigest,
                operatorAuthorityEpoch,
                operatorDecision),
            decision.Mode));

    }

    private static CovenantEgressWardDecision Denied(CovenantEgressAuthorization authorization) =>
        new(
            authorization,
            CovenantToolRiskIdentity.CovenantSensitiveEgress,
            CovenantAuthorizationMode.None);

}
