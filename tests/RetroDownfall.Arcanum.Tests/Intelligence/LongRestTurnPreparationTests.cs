using Microsoft.AspNetCore.Http;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Covenant;

using RetroDownfall.Arcanum.Core.Intelligence;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

public sealed class LongRestTurnPreparationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Long_rest_alone_observes_pending_claim_ownership_before_any_maintenance(bool campaign)
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        settings.Features.CampaignRollups = false;

        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        RefusingClaims claims = new();

        CanonicalCampaignContext scope = campaign ? CampaignRollupTurnSnapshotTests.Campaign : CanonicalCampaignContext.GlobalOnly;

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(
            ArcanumExecutionSurface.SessionBackedOperatorTurn, scope, InvocationAttendance.Attended,
            CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        CampaignRollupTurnPreparer preparer = new(store, new TestOptionsMonitor<ArcanumSettings>(settings),
            new CampaignRollupTurnSnapshotTests.Resolver(), null!,
            UnreachedLongRestPorts.Coordinator, UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery, UnreachedLongRestPorts.Configuration, UnreachedLongRestPorts.RootEnricher,
            new CampaignRollupTurnSnapshotTests.RefusingMaintenance(),
            new CampaignRollupTurnSnapshotTests.InputStore(), null, claims, new CampaignRollupTurnSnapshotTests.Authority());

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Continue.", SessionId: Guid.Parse("3f2e1d0c-9b8a-4756-8493-a2b1c0d9e8f7")), invocation, false, CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Hub.SessionTurnBusy, result.Error.Code);

        Assert.Equal(1, claims.Acquisitions);

        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public void Long_rest_alone_makes_a_first_canonical_campaign_turn_claimed_before_headers_are_sealed()
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        settings.Features.CampaignRollups = false;

        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings))
            .BuildServiceProvider();

        DefaultHttpContext http = new() { RequestServices = services };

        PingRequest request = new("Continue.", CampaignId: CampaignRollupTurnSnapshotTests.CampaignId);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, request, CampaignRollupTurnSnapshotTests.Campaign);

        Assert.Equal(ArcanumExecutionSurface.SessionBackedOperatorTurn, invocation.Surface);

        Assert.True(CovenantRequestFeatures.IsProtectedResponse(http));

        Assert.False(invocation.CanReadCovenant);
    }

    [Fact]
    public void Long_rest_alone_makes_a_first_Global_turn_private_before_any_preparation_await_or_stream_write()
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        settings.Features.CampaignRollups = false;

        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings))
            .BuildServiceProvider();

        DefaultHttpContext http = new() { RequestServices = services };

        PingRequest request = new("Continue.", SessionId: null);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, request, CanonicalCampaignContext.GlobalOnly);

        // ForTurn returns synchronously, before preparation can await or a heartbeat can seal headers.
        Assert.Null(request.SessionId);

        Assert.Equal(ArcanumExecutionSurface.SessionBackedOperatorTurn, invocation.Surface);

        Assert.Equal(CanonicalCampaignContext.GlobalOnly, invocation.Campaign);

        Assert.True(CovenantRequestFeatures.IsProtectedResponse(http));

        Assert.False(http.Response.HasStarted);

        Assert.False(invocation.CanReadCovenant);

        CovenantProtectedResponseHeaders.ApplyStreamingDefaultWithoutWeakening(http);

        Assert.Equal("no-store, private", http.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", http.Response.Headers.Pragma.ToString());

        Assert.Equal("0", http.Response.Headers.Expires.ToString());
    }

    [Theory]
    [InlineData(ArcanumExecutionSurface.StatelessOperatorTurn)]
    [InlineData(ArcanumExecutionSurface.ContextInspection)]
    [InlineData(ArcanumExecutionSurface.InternalBackground)]
    public async Task Long_rest_enabled_ineligible_surfaces_create_no_claim_or_rollup_read(ArcanumExecutionSurface surface)
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        CampaignRollupTurnSnapshotTests.ProbeStore store = new();

        RefusingClaims claims = new();

        CampaignRollupTurnPreparer preparer = new(store, new TestOptionsMonitor<ArcanumSettings>(settings),
            new CampaignRollupTurnSnapshotTests.Resolver(), null!,
            UnreachedLongRestPorts.Coordinator, UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery, UnreachedLongRestPorts.Configuration, UnreachedLongRestPorts.RootEnricher,
            new CampaignRollupTurnSnapshotTests.RefusingMaintenance(),
            new CampaignRollupTurnSnapshotTests.InputStore(), null, claims, new CampaignRollupTurnSnapshotTests.Authority());

        ArcanumInvocationContext invocation = ArcanumInvocationContext.Create(surface,
            CampaignRollupTurnSnapshotTests.Campaign, InvocationAttendance.Attended, CovenantContextPolicy.Default,
            ToolPolicy.NoTools, null).Value;

        Result<CampaignRollupTurnSnapshot?> result = await preparer.PrepareLiveAsync(
            new("Inspect.", SessionId: Guid.NewGuid()), invocation, false, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Null(result.Value);

        Assert.Equal(0, claims.Acquisitions);

        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task Explicit_campaign_resolution_loads_registered_root_facts_through_the_enricher_after_successful_scope_resolution()
    {
        RootReader roots = new();

        CanonicalCampaignContextResolver resolver = new(new BindingReader(), roots, new AvailabilityReader());

        Result<CanonicalCampaignContext> resolved = await resolver.ResolveAsync(
            new(null, RootReader.Campaign, null), CancellationToken.None);

        // Scope resolution answers the table and reads no root: the Long Rest-scoped enrichment that follows does.
        Assert.True(resolved.IsSuccess);

        Assert.Null(resolved.Value.PathIdentityRevision);

        Assert.Empty(roots.RootReads);

        ArcanumSettings enabled = new();

        enabled.Features.LongRest = true;

        Result<CanonicalCampaignContext> result = await new CanonicalCampaignRootEnricher(roots, new TestOptionsMonitor<ArcanumSettings>(enabled))
            .EnrichAsync(resolved.Value, CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(7, result.Value.PathIdentityRevision);

        Assert.Equal(RootReader.RootDigest, result.Value.RootIdentityDigest);

        Assert.Equal(12, result.Value.CampaignAvailabilityGeneration);

        Assert.Equal(new[] { RootReader.Campaign }, roots.RootReads);
    }

    [Fact]
    public async Task Conflicting_explicit_campaign_and_workspace_cannot_fetch_an_authoritative_root()
    {
        RootReader roots = new() { ConflictingWorkspace = true };

        CanonicalCampaignContextResolver resolver = new(new BindingReader(), roots, new AvailabilityReader());

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new(null, RootReader.Campaign, "/controlled/conflicting-workspace"), CancellationToken.None);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, result.Error.Code);

        Assert.Empty(roots.RootReads);
    }

    private sealed class RefusingClaims : ISessionTurnClaimCoordinator
    {
        public int Acquisitions { get; private set; }

        public ValueTask<Result<SessionTurnClaimLease>> AcquireAsync(SessionTurnRequestIdentity request, CancellationToken cancellationToken)
        {
            Acquisitions++;

            return ValueTask.FromResult(Result<SessionTurnClaimLease>.Failure(new Error(ErrorCodes.Hub.SessionTurnBusy, "An earlier turn owns this Session.")));
        }

        public ValueTask<Result<SessionTurnClaim>> MarkBegunAsync(SessionTurnClaimLease lease, AssistantReplyBeginReceipt begin, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Claim refusal must precede assistant begin.");

        public ValueTask<Result<SessionTurnClaim>> CompleteAsync(SessionTurnClaimLease lease, SessionTurnClaimOutcome outcome, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A refused claim cannot be completed by this request.");
    }

    private sealed class BindingReader : ISessionCampaignBindingReader
    {
        public ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(Guid? sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionCampaignBindingRecord?>.Success(null));
    }

    private sealed class AvailabilityReader : ICampaignAvailabilityReader
    {
        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<long?>.Success(12));
    }

    private sealed class RootReader : ICampaignPathIdentityReader
    {
        internal static readonly Guid Campaign = Guid.Parse("7e1d9c42-05b8-4f63-9a0e-3c8b27d641f5");

        internal static readonly CovenantDigest RootDigest = new(Enumerable.Repeat((byte)0x4A, 32).ToArray());

        public bool ConflictingWorkspace { get; init; }

        public List<Guid> RootReads { get; } = [];

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(ConflictingWorkspace
                ? new(Guid.Parse("1a2b3c4d-5e6f-4071-8293-a4b5c6d7e8f9"), 1, 3, 2, RootDigest) : null));

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            RootReads.Add(campaignId);

            return ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(new(Campaign, 1, 7, 2, RootDigest)));
        }
    }
}
