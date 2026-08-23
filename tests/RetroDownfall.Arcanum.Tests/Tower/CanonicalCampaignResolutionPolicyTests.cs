using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// Every row of the approved fill-and-verify table, as a literal case.
/// </summary>
/// <remarks>
/// The policy is pure, so each row is a direct call with no database, no filesystem, and no fixture.
/// That is the point of extracting it: the old behaviour was untestable in isolation because it
/// interleaved a repository lookup with the decision it was making.
/// </remarks>
public sealed class CanonicalCampaignResolutionPolicyTests
{

    private static readonly Guid CampaignC = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid CampaignD = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private const long Generation = 3;

    [Fact]
    public void ExistingCampaignSession_AcceptsMatchingSources()
    {

        foreach ((Guid? explicitId, RegisteredCampaignIdentity? workspace, bool supplied) in new[]
        {
            ((Guid?)null, (RegisteredCampaignIdentity?)null, false),
            (CampaignC, null, false),
            (null, Registered(CampaignC), true),
            (CampaignC, Registered(CampaignC), true),
        })
        {

            Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
                SessionCampaignBinding.ForCampaign(CampaignC),
                explicitId,
                workspace,
                supplied,
                Generation);

            Assert.True(result.IsSuccess);
            Assert.Equal(CampaignC, result.Value.CampaignId);
            Assert.Equal(Generation, result.Value.CampaignAvailabilityGeneration);

        }

    }

    [Fact]
    public void ExistingCampaignSession_RejectsADifferentExplicitCampaign()
    {

        Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.ForCampaign(CampaignC),
            CampaignD,
            workspace: null,
            workingDirectorySupplied: false,
            Generation);

        AssertConflict(result);

    }

    [Fact]
    public void ExistingCampaignSession_RejectsADifferentRegisteredWorkspace()
    {

        Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.ForCampaign(CampaignC),
            explicitCampaignId: null,
            Registered(CampaignD),
            workingDirectorySupplied: true,
            Generation);

        AssertConflict(result);

    }

    [Fact]
    public void ExistingGlobalOnlySession_AcceptsNothingAndStaysGlobal()
    {

        Result<CanonicalCampaignContext> clean = CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.GlobalOnly,
            explicitCampaignId: null,
            workspace: null,
            workingDirectorySupplied: false,
            campaignAvailabilityGeneration: null);

        Assert.True(clean.IsSuccess);
        Assert.False(clean.Value.IsCampaignBound);

        AssertConflict(CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.GlobalOnly,
            CampaignC,
            workspace: null,
            workingDirectorySupplied: false,
            Generation));

        AssertConflict(CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.GlobalOnly,
            explicitCampaignId: null,
            Registered(CampaignC),
            workingDirectorySupplied: true,
            Generation));

    }

    [Fact]
    public void LegacyUnresolvedSession_FailsWithBindingRequired()
    {

        foreach (Guid? explicitId in new Guid?[] { null, CampaignC })
        {

            Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
                SessionCampaignBinding.LegacyUnresolved,
                explicitId,
                workspace: null,
                workingDirectorySupplied: false,
                Generation);

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCodes.Session.CampaignBindingRequired, result.Error.Code);

        }

    }

    [Fact]
    public void NoSession_UsesTheExplicitCampaign()
    {

        Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            CampaignC,
            workspace: null,
            workingDirectorySupplied: false,
            Generation);

        Assert.True(result.IsSuccess);
        Assert.Equal(CampaignC, result.Value.CampaignId);

    }

    [Fact]
    public void NoSession_UsesTheRegisteredWorkspaceAndCapturesItsRevision()
    {

        RegisteredCampaignIdentity workspace = Registered(CampaignC, revision: 9);

        Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            explicitCampaignId: null,
            workspace,
            workingDirectorySupplied: true,
            Generation);

        Assert.True(result.IsSuccess);
        Assert.Equal(CampaignC, result.Value.CampaignId);
        Assert.Equal(9, result.Value.PathIdentityRevision);
        Assert.Equal(workspace.PhysicalIdentityDigest, result.Value.RootIdentityDigest);

    }

    [Fact]
    public void NoSession_RejectsAnExplicitCampaignInsideADifferentRegisteredRoot()
    {

        AssertConflict(CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            CampaignC,
            Registered(CampaignD),
            workingDirectorySupplied: true,
            Generation));

    }

    [Fact]
    public void NoSession_AndNoSource_ResolvesGlobalOnly()
    {

        Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            explicitCampaignId: null,
            workspace: null,
            workingDirectorySupplied: false,
            campaignAvailabilityGeneration: null);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsCampaignBound);
        Assert.Null(result.Value.CampaignAvailabilityGeneration);

    }

    [Fact]
    public void AnUnregisteredDirectoryContributesNothingUntilACampaignIsEstablished()
    {

        // Before a Campaign exists, an unregistered path is simply a directory Arcanum does not manage.
        Result<CanonicalCampaignContext> ignored = CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            explicitCampaignId: null,
            workspace: null,
            workingDirectorySupplied: true,
            campaignAvailabilityGeneration: null);

        Assert.True(ignored.IsSuccess);
        Assert.False(ignored.Value.IsCampaignBound);

        // Once one exists, the same path is a claim to be running outside it, which is an escape.
        AssertConflict(CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            CampaignC,
            workspace: null,
            workingDirectorySupplied: true,
            Generation));

        AssertConflict(CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.ForCampaign(CampaignC),
            explicitCampaignId: null,
            workspace: null,
            workingDirectorySupplied: true,
            Generation));

    }

    [Fact]
    public void ADeletedCampaignFailsRatherThanResolving()
    {

        Result<CanonicalCampaignContext> result = CanonicalCampaignResolutionPolicy.Resolve(
            SessionCampaignBinding.ForCampaign(CampaignC),
            explicitCampaignId: null,
            workspace: null,
            workingDirectorySupplied: false,
            campaignAvailabilityGeneration: null);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Campaign.NotFound, result.Error.Code);

    }

    [Fact]
    public void AnEmptyExplicitCampaignIsRefused()
    {

        AssertConflict(CanonicalCampaignResolutionPolicy.Resolve(
            session: null,
            Guid.Empty,
            workspace: null,
            workingDirectorySupplied: false,
            Generation));

    }

    private static void AssertConflict(Result<CanonicalCampaignContext> result)
    {

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, result.Error.Code);

    }

    private static RegisteredCampaignIdentity Registered(Guid campaignId, long revision = 1) =>
        new(
            campaignId,
            CampaignPathIdentityPolicy.Version,
            revision,
            Depth: 3,
            new CovenantDigest(System.Security.Cryptography.SHA256.HashData(campaignId.ToByteArray())));

}
