using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// Builders for the two erasure-authority arms and the labelled artifacts they act on.
/// </summary>
/// <remarks>
/// Real leases from the real gate rather than a stub lease: the whole point of the authority is that
/// it borrows a live capability, and a hand-rolled lease would be a capability nobody acquired.
/// </remarks>
internal static class CovenantErasureAuthorityFixture
{

    internal static readonly DateTimeOffset LabelledAt = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    internal static ArtifactSensitivityLabel Label(
        Guid artifactId,
        Guid labelId,
        SensitiveArtifactKind kind = SensitiveArtifactKind.AssistantEntry,
        Guid? sessionId = null,
        Guid? campaignId = null,
        ulong revision = 0,
        byte contentSeed = 0x11) =>
        new(
            labelId,
            kind,
            artifactId,
            sessionId,
            campaignId,
            turnId: null,
            revision,
            CovenantOperationGateFixture.Digest(contentSeed),
            ContentSensitivity.CovenantDerived,
            GenerationProvenance.Create([CovenantOperationGateFixture.DatasetGeneration]),
            producingPlanDigest: null,
            producingAdmissionDigest: null,
            producingMaintenanceReceiptDigest: null,
            LabelledAt);

    internal static CovenantProtectedArtifactErasureItem Item(
        Guid artifactId,
        Guid labelId,
        SensitiveArtifactKind kind = SensitiveArtifactKind.AssistantEntry,
        Guid? sessionId = null,
        Guid? campaignId = null,
        ulong revision = 0,
        byte contentSeed = 0x11) =>
        new(
            artifactId,
            kind,
            sessionId,
            labelId,
            Label(artifactId, labelId, kind, sessionId, campaignId, revision, contentSeed),
            CovenantOperationGateFixture.Digest(contentSeed),
            revision);

    internal static OperatorAuthorityContext OperatorContext(
        FakeCovenantAuthorityProvider authority,
        CovenantAuthorityRequirement requirement = CovenantAuthorityRequirement.SensitivityRetentionPurge) =>
        new OperatorAuthorityContextIssuer(authority).Issue(requirement).Value;

    internal static IOperatorAuthorityContextIssuer Issuer(FakeCovenantAuthorityProvider authority) =>
        new OperatorAuthorityContextIssuer(authority);

}

/// <summary>
/// An issuer whose revalidation can be failed on demand, for the "authority moved mid-erasure" arm.
/// </summary>
internal sealed class RevocableOperatorAuthorityIssuer(FakeCovenantAuthorityProvider authority)
    : IOperatorAuthorityContextIssuer
{

    private readonly OperatorAuthorityContextIssuer _inner = new(authority);

    internal bool RevalidationFails { get; set; }

    internal int RevalidationCount { get; private set; }

    public Result<OperatorAuthorityContext> Issue(CovenantAuthorityRequirement requirement) =>
        _inner.Issue(requirement);

    public Result<CovenantReadAuthorityEpoch> IssueReadEpoch() => _inner.IssueReadEpoch();

    public Result Revalidate(OperatorAuthorityContext context)
    {

        RevalidationCount++;

        return RevalidationFails
            ? Result.Failure(
                new Error(ErrorCodes.Covenant.StaleSnapshot, "Operator authority moved underneath this erasure."))
            : _inner.Revalidate(context);

    }

}
