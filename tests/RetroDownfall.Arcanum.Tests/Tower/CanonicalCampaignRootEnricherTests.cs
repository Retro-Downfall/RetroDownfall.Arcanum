using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// Long Rest-scoped enrichment: the registered root of a Campaign is added to an already-resolved context, only while Long Rest is
/// enabled, and only for the Campaign the context names.
/// </summary>
public sealed class CanonicalCampaignRootEnricherTests
{
    private static readonly Guid CampaignC = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid CampaignD = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private static CancellationToken Token => CancellationToken.None;

    private static readonly CovenantDigest RootDigest = new(Enumerable.Repeat((byte)0x4A, 32).ToArray());

    private static readonly CovenantDigest OtherDigest = new(Enumerable.Repeat((byte)0x5B, 32).ToArray());

    private static RegisteredCampaignIdentity Root(Guid campaign, uint policy = 3, long revision = 7, CovenantDigest? digest = null) =>
        new(campaign, policy, revision, Depth: 1, digest ?? RootDigest);

    // What the resolver answers when no working directory was supplied: a policy version and no root.
    private static CanonicalCampaignContext Unobserved(Guid campaign = default) => CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(campaign == default ? CampaignC : campaign), 12, CampaignPathIdentityPolicy.Version, null, null);

    // What it answers when a working directory resolved to the Campaign's root: that root's policy version, revision and digest.
    private static CanonicalCampaignContext Observed(RegisteredCampaignIdentity workspace) => CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(workspace.CampaignId), 12, workspace.PolicyVersion, workspace.Revision, workspace.PhysicalIdentityDigest);

    private static CanonicalCampaignRootEnricher Enricher(StubPaths paths, bool longRest = true)
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = longRest;

        return new(paths, new TestOptionsMonitor<ArcanumSettings>(settings));
    }

    [Fact]
    public async Task A_campaign_resolved_without_a_working_directory_gains_the_registered_root()
    {
        StubPaths paths = new() { Root = Root(CampaignC) };

        Result<CanonicalCampaignContext> result = await Enricher(paths).EnrichAsync(Unobserved(), Token);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Code : null);
        Assert.Equal(1, paths.Calls);

        // Exactly the facts the fence compares with the live registry, and nothing else of the context changed.
        Assert.Equal(CampaignC, result.Value.CampaignId);
        Assert.Equal(12, result.Value.CampaignAvailabilityGeneration);
        Assert.Equal(3u, result.Value.PathIdentityPolicyVersion);
        Assert.Equal(7, result.Value.PathIdentityRevision);
        Assert.Equal(RootDigest, result.Value.RootIdentityDigest);
        Assert.Equal(CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(CampaignC), 12, 3, 7, RootDigest), result.Value);
    }

    [Fact]
    public async Task An_observed_workspace_that_is_still_the_registered_root_enriches_to_itself()
    {
        RegisteredCampaignIdentity root = Root(CampaignC);

        Result<CanonicalCampaignContext> result = await Enricher(new StubPaths { Root = root }).EnrichAsync(Observed(root), Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(Observed(root), result.Value);

        // Enrichment is idempotent: enriching its own answer changes nothing.
        Result<CanonicalCampaignContext> again = await Enricher(new StubPaths { Root = root }).EnrichAsync(result.Value, Token);

        Assert.Equal(result.Value, again.Value);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("digest")]
    [InlineData("policy")]
    public async Task An_observed_workspace_that_differs_from_the_registered_root_is_a_stale_snapshot(string difference)
    {
        RegisteredCampaignIdentity observed = Root(CampaignC);

        RegisteredCampaignIdentity registered = difference switch
        {
            "revision" => observed with { Revision = 8 },
            "digest" => observed with { PhysicalIdentityDigest = OtherDigest },
            _ => observed with { PolicyVersion = 4 },
        };

        Result<CanonicalCampaignContext> result = await Enricher(new StubPaths { Root = registered }).EnrichAsync(Observed(observed), Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
        Assert.Equal("The registered Campaign root changed during resolution.", result.Error.Message);
    }

    [Fact]
    public async Task A_root_that_vanished_is_stale_only_when_a_working_directory_claimed_it()
    {
        // A working directory resolved to this Campaign's root, and the registry no longer has it.
        Result<CanonicalCampaignContext> vanished = await Enricher(new StubPaths()).EnrichAsync(Observed(Root(CampaignC)), Token);

        Assert.True(vanished.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, vanished.Error.Code);
        Assert.Equal("The resolved Campaign root is no longer registered.", vanished.Error.Message);

        // Nothing claimed a root, and a Campaign may exist with none registered: the context is exactly what it was.
        CanonicalCampaignContext unobserved = Unobserved();

        Result<CanonicalCampaignContext> unregistered = await Enricher(new StubPaths()).EnrichAsync(unobserved, Token);

        Assert.True(unregistered.IsSuccess);
        Assert.Equal(unobserved, unregistered.Value);
        Assert.Null(unregistered.Value.PathIdentityRevision);
    }

    [Theory]
    [InlineData("zero-policy")]
    [InlineData("zero-revision")]
    [InlineData("negative-revision")]
    [InlineData("invalid-digest")]
    [InlineData("foreign-campaign")]
    public async Task A_registered_root_that_cannot_be_trusted_is_a_stale_snapshot_and_not_an_enrichment(string defect)
    {
        RegisteredCampaignIdentity registered = defect switch
        {
            "zero-policy" => Root(CampaignC, policy: 0),
            "zero-revision" => Root(CampaignC, revision: 0),
            "negative-revision" => Root(CampaignC, revision: -1),
            "invalid-digest" => Root(CampaignC) with { PhysicalIdentityDigest = default },
            _ => Root(CampaignD),
        };

        Result<CanonicalCampaignContext> result = await Enricher(new StubPaths { Root = registered }).EnrichAsync(Unobserved(), Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);
    }

    [Fact]
    public async Task A_registry_read_failure_is_propagated_rather_than_treated_as_unregistered()
    {
        StubPaths paths = new() { Failure = new Error(ErrorCodes.Covenant.IntegrityFailure, "malformed") };

        Result<CanonicalCampaignContext> result = await Enricher(paths).EnrichAsync(Unobserved(), Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
    }

    [Fact]
    public async Task A_global_context_is_returned_unchanged_without_a_registry_read()
    {
        StubPaths paths = new() { Root = Root(CampaignC) };

        Result<CanonicalCampaignContext> result = await Enricher(paths).EnrichAsync(CanonicalCampaignContext.GlobalOnly, Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(CanonicalCampaignContext.GlobalOnly, result.Value);
        Assert.Equal(0, paths.Calls);
    }

    [Theory]
    [InlineData("unobserved")]
    [InlineData("observed")]
    [InlineData("global")]
    public async Task With_Long_Rest_off_the_context_is_exactly_what_the_resolver_returned_and_no_registry_is_read(string shape)
    {
        RegisteredCampaignIdentity registered = Root(CampaignC);

        // A registry that would fail every one of the checks above if it were read.
        StubPaths paths = new() { Root = registered with { Revision = 99, PhysicalIdentityDigest = OtherDigest }, Failure = new Error(ErrorCodes.Covenant.IntegrityFailure, "must not be read") };

        CanonicalCampaignContext context = shape switch
        {
            "unobserved" => Unobserved(),
            "observed" => Observed(registered),
            _ => CanonicalCampaignContext.GlobalOnly,
        };

        Result<CanonicalCampaignContext> result = await Enricher(paths, longRest: false).EnrichAsync(context, Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(context, result.Value);
        Assert.Equal(0, paths.Calls);
    }

    private sealed class StubPaths : ICampaignPathIdentityReader
    {
        public RegisteredCampaignIdentity? Root { get; init; }

        public Error? Failure { get; init; }

        public int Calls { get; private set; }

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Enrichment never resolves a working directory.");

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(Failure is { } error
                ? Result<RegisteredCampaignIdentity?>.Failure(error)
                : Result<RegisteredCampaignIdentity?>.Success(Root));
        }
    }
}
