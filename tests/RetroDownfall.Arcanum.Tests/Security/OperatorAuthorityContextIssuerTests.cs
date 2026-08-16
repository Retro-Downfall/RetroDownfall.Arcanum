using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Tests.Security;

/// <summary>
/// The one seam that mints operator authority, and every state in which it must refuse.
/// </summary>
public sealed class OperatorAuthorityContextIssuerTests
{

    private static readonly Guid Installation = Guid.Parse("2C4A5E3B-9F17-4D0C-8A6E-1B3D5F70921A");

    [Fact]
    public void Issue_binds_the_requirement_epoch_key_version_and_installation()
    {

        OperatorAuthorityContextIssuer issuer = new(new StubAuthority(Clean()));

        Result<OperatorAuthorityContext> issued = issuer.Issue(CovenantAuthorityRequirement.CovenantManage);

        Assert.True(issued.IsSuccess);
        Assert.Equal(CovenantAuthorityRequirement.CovenantManage, issued.Value.Requirement);
        Assert.Equal(11, issued.Value.AuthorityEpoch);
        Assert.Equal(4u, issued.Value.MasterKeyVersion);
        Assert.Equal(Installation.ToString().ToUpperInvariant(), issued.Value.InstallationIdentity);

    }

    [Fact]
    public void Issue_mints_a_fresh_nonce_per_issuance()
    {

        OperatorAuthorityContextIssuer issuer = new(new StubAuthority(Clean()));

        Guid first = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value.IssuerNonce;

        Guid second = issuer.Issue(CovenantAuthorityRequirement.ProtectedRead).Value.IssuerNonce;

        Assert.NotEqual(first, second);
        Assert.NotEqual(Guid.Empty, first);

    }

    [Fact]
    public void Issue_refuses_before_authority_is_established()
    {

        OperatorAuthorityContextIssuer issuer = new(new StubAuthority(null));

        Result<OperatorAuthorityContext> issued = issuer.Issue(CovenantAuthorityRequirement.LifecycleManage);

        Assert.False(issued.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, issued.Error.Code);

        Assert.False(issuer.IssueReadEpoch().IsSuccess);

    }

    [Theory]
    [InlineData(CovenantHostToolsState.PendingHostToolsTaint)]
    [InlineData(CovenantHostToolsState.HostToolsTainted)]
    public void Issue_refuses_a_tainted_installation_for_every_requirement(CovenantHostToolsState state)
    {

        OperatorAuthorityContextIssuer issuer = new(new StubAuthority(Snapshot(state)));

        foreach (CovenantAuthorityRequirement requirement in Enum.GetValues<CovenantAuthorityRequirement>())
        {

            Result<OperatorAuthorityContext> issued = issuer.Issue(requirement);

            Assert.False(issued.IsSuccess);
            Assert.Equal(ErrorCodes.Covenant.OperatorAuthorityUnavailable, issued.Error.Code);

        }

        Assert.False(issuer.IssueReadEpoch().IsSuccess);

    }

    [Fact]
    public void Issue_refuses_an_undefined_requirement()
    {

        OperatorAuthorityContextIssuer issuer = new(new StubAuthority(Clean()));

        Result<OperatorAuthorityContext> issued = issuer.Issue((CovenantAuthorityRequirement)99);

        Assert.False(issued.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.ForbiddenAuthority, issued.Error.Code);

    }

    [Fact]
    public void Revalidate_rejects_a_context_whose_epoch_or_key_version_moved()
    {

        StubAuthority authority = new(Clean());

        OperatorAuthorityContextIssuer issuer = new(authority);

        OperatorAuthorityContext context = issuer.Issue(CovenantAuthorityRequirement.CovenantManage).Value;

        Assert.True(issuer.Revalidate(context).IsSuccess);

        authority.Current = Clean() with { AuthorityEpoch = 12 };

        Result stale = issuer.Revalidate(context);

        Assert.False(stale.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, stale.Error.Code);

        authority.Current = Clean() with { MasterKeyVersion = 5 };

        Assert.False(issuer.Revalidate(context).IsSuccess);

        authority.Current = Snapshot(CovenantHostToolsState.HostToolsTainted);

        Assert.False(issuer.Revalidate(context).IsSuccess);

    }

    [Fact]
    public void IssueReadEpoch_carries_the_clean_generation_and_tracks_it()
    {

        StubAuthority authority = new(Clean());

        OperatorAuthorityContextIssuer issuer = new(authority);

        Result<CovenantReadAuthorityEpoch> epoch = issuer.IssueReadEpoch();

        Assert.True(epoch.IsSuccess);
        Assert.Equal(11, epoch.Value.AuthorityEpoch);
        Assert.True(epoch.Value.Matches(authority.Current));

        authority.Current = Clean() with { AuthorityEpoch = 12 };

        Assert.False(epoch.Value.Matches(authority.Current));

    }

    private static CovenantAuthoritySnapshot Clean() => Snapshot(CovenantHostToolsState.Clean);

    private static CovenantAuthoritySnapshot Snapshot(CovenantHostToolsState state) =>
        new(
            Installation.ToString().ToUpperInvariant(),
            AuthorityEpoch: 11,
            MasterKeyVersion: 4,
            RecoveryEnvelopeEpoch: 2,
            state,
            state == CovenantHostToolsState.Clean
                ? null
                : "A1B2C3D4-E5F6-4708-9A0B-1C2D3E4F5061");

    private sealed class StubAuthority(CovenantAuthoritySnapshot? current) : ICovenantAuthoritySnapshotProvider
    {

        public CovenantAuthoritySnapshot? Current { get; set; } = current;

    }

}
