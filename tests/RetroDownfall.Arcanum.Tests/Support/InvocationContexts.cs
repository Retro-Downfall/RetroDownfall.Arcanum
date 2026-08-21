using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;

namespace RetroDownfall.Arcanum.Tests.Support;

/// <summary>
/// The invocation contexts tests use, named for what they classify rather than how they are built.
/// </summary>
/// <remarks>
/// Deliberately not a single "test context" constant. Which surface a test invokes is part of what it
/// is asserting, and a shared default would let a test pass because it silently ran as something other
/// than the surface it names.
/// </remarks>
internal static class InvocationContexts
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    private static readonly Guid Campaign = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    /// <summary>An attended, session-backed, Campaign-bound operator turn: the fully eligible case.</summary>
    internal static ArcanumInvocationContext AttendedSession(Guid? campaignId = null) =>
        Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignContext(campaignId ?? Campaign),
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            Epoch());

    /// <summary>An attended stateless operator turn: eligible context, no mutation tool.</summary>
    internal static ArcanumInvocationContext StatelessOperator() =>
        Create(
            ArcanumExecutionSurface.StatelessOperatorTurn,
            CanonicalCampaignContext.GlobalOnly,
            InvocationAttendance.Attended,
            CovenantContextPolicy.Default,
            ToolPolicy.AllTools,
            Epoch());

    /// <summary>An authenticated context inspection: builds a plan, dispatches nothing.</summary>
    internal static ArcanumInvocationContext Inspection() =>
        Create(
            ArcanumExecutionSurface.ContextInspection,
            CanonicalCampaignContext.GlobalOnly,
            InvocationAttendance.Unattended,
            CovenantContextPolicy.Default,
            ToolPolicy.NoTools,
            Epoch());

    /// <summary>An operator turn that arrived with an explicit no-context policy.</summary>
    internal static ArcanumInvocationContext NoContextSession() =>
        Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn,
            CampaignContext(Campaign),
            InvocationAttendance.Attended,
            CovenantContextPolicy.None,
            ToolPolicy.AllTools,
            Epoch());

    private static CovenantReadAuthorityEpoch Epoch() =>
        CovenantReadAuthorityEpoch.CreateForTests(
            Installation,
            runtimeAuthorityGeneration: 1,
            authorityEpoch: 11);

    private static CanonicalCampaignContext CampaignContext(Guid campaignId) =>
        CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);

    private static ArcanumInvocationContext Create(
        ArcanumExecutionSurface surface,
        CanonicalCampaignContext campaign,
        InvocationAttendance attendance,
        CovenantContextPolicy contextPolicy,
        ToolPolicy toolPolicy,
        CovenantReadAuthorityEpoch epoch)
    {

        Result<ArcanumInvocationContext> created = ArcanumInvocationContext.Create(
            surface,
            campaign,
            attendance,
            contextPolicy,
            toolPolicy,
            epoch);

        return created.IsSuccess
            ? created.Value
            : throw new InvalidOperationException(created.Error.Message);

    }

}
