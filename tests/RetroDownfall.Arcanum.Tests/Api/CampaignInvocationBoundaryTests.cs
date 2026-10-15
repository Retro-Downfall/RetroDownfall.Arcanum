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
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Intelligence;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class CampaignInvocationBoundaryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Eligible_Campaign_turns_choose_durable_Session_authority_and_private_headers_before_any_work(bool existingSession, bool emptyStatelessList)
    {
        using ServiceProvider services = Services(true);

        DefaultHttpContext http = new() { RequestServices = services };

        PingRequest request = new("continue", SessionId: existingSession ? Guid.NewGuid() : null,
            StatelessMessages: emptyStatelessList ? [] : null);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, request, Campaign());

        Assert.Equal(ArcanumExecutionSurface.SessionBackedOperatorTurn, invocation.Surface);

        Assert.True(CovenantRequestFeatures.IsProtectedResponse(http));

        CovenantProtectedResponseHeaders.ApplyStreamingDefaultWithoutWeakening(http);

        Assert.Equal("no-store, private", http.Response.Headers.CacheControl.ToString());

        Assert.Equal("no-cache", http.Response.Headers.Pragma.ToString());

        Assert.Equal("0", http.Response.Headers.Expires.ToString());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-context")]
    [InlineData("stateless")]
    [InlineData("global")]
    public void Ineligible_new_turns_keep_their_existing_surface_and_cache_behavior(string exclusion)
    {
        using ServiceProvider services = Services(exclusion != "disabled");

        DefaultHttpContext http = new() { RequestServices = services };

        if (exclusion == "no-context")
        {
            http.Items[ArcanumInvocationContexts.NoContextRequestFeatureKey] = true;
        }

        PingRequest request = new("continue", StatelessMessages: exclusion == "stateless" ? [new("user", "continue")] : null);

        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, request,
            exclusion == "global" ? null : Campaign());

        Assert.Equal(ArcanumExecutionSurface.StatelessOperatorTurn, invocation.Surface);

        Assert.False(CovenantRequestFeatures.IsProtectedResponse(http));
    }

    private static readonly Guid BoundCampaign = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid BoundSession = Guid.Parse("3F2E1D0C-9B8A-4756-8493-A2B1C0D9E8F7");

    private static readonly CovenantDigest RegisteredDigest = new(Enumerable.Repeat((byte)0x4A, 32).ToArray());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_turn_boundary_gives_the_campaign_its_registered_root_only_while_Long_Rest_is_on_and_reads_nothing_otherwise(bool longRest)
    {
        // A Session bound to a Campaign whose root is registered, and a request that supplies no working directory: the case where the
        // resolver's answer carries no root.
        CountingPaths paths = new();

        ArcanumSettings settings = new();

        settings.Features.LongRest = longRest;

        settings.Features.CampaignRollups = true;

        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings))
            .AddSingleton<ISessionCampaignBindingReader>(new BoundSessions())
            .AddSingleton<ICampaignPathIdentityReader>(paths)
            .AddSingleton<ICampaignAvailabilityReader>(new FixedAvailability())
            .AddSingleton<ICanonicalCampaignContextResolver>(static sp => new CanonicalCampaignContextResolver(
                sp.GetRequiredService<ISessionCampaignBindingReader>(), sp.GetRequiredService<ICampaignPathIdentityReader>(), sp.GetRequiredService<ICampaignAvailabilityReader>()))
            .AddSingleton<ICanonicalCampaignRootEnricher>(static sp => new CanonicalCampaignRootEnricher(
                sp.GetRequiredService<ICampaignPathIdentityReader>(), sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>()))
            .AddSingleton(System.Reflection.DispatchProxy.Create<ICampaignRepository, UnreachedLongRestProxy>())
            .BuildServiceProvider();

        // The Session-bound request is a Long Rest turn candidate: a plain default-context Session turn.
        Result<CanonicalPingTurn> turn = await CanonicalPingResolution.ResolveTurnAsync(
            new("continue", SessionId: BoundSession), new DefaultHttpContext { RequestServices = services }, CancellationToken.None);

        Assert.True(turn.IsSuccess, turn.IsFailure ? turn.Error.Code : null);

        CanonicalCampaignContext policyAnswer = CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(BoundCampaign), 12, CampaignPathIdentityPolicy.Version, null, null);

        if (longRest)
        {
            Assert.Equal(1, paths.FindCalls);

            Assert.Equal(CanonicalCampaignContext.Create(SessionCampaignBinding.ForCampaign(BoundCampaign), 12, 3, 7, RegisteredDigest), turn.Value.Campaign);
        }
        else
        {
            // Byte-identical to the answer before Long Rest existed, and the registry was not read.
            Assert.Equal(0, paths.FindCalls);

            Assert.Equal(policyAnswer, turn.Value.Campaign);

            Assert.Null(turn.Value.Campaign.PathIdentityRevision);
        }

        // The invocation carries exactly the context the boundary answered, which is what the turn lease and the capability will agree with.
        DefaultHttpContext http = new() { RequestServices = services };

        Assert.Equal(turn.Value.Campaign, ArcanumInvocationContexts.ForTurn(http, turn.Value.Request, turn.Value.Campaign).Campaign);
    }

    [Fact]
    public async Task A_root_that_moved_under_the_turn_boundary_ends_the_request_only_while_Long_Rest_is_on()
    {
        foreach (bool longRest in new[] { true, false })
        {
            ArcanumSettings settings = new();

            settings.Features.LongRest = longRest;

            // The working directory resolved to the root at revision 7; the registry has since moved on to revision 8.
            RegisteredCampaignIdentity observed = new(BoundCampaign, 3, 7, 1, RegisteredDigest);

            CountingPaths paths = new() { Workspace = observed, Root = observed with { Revision = 8 } };

            using ServiceProvider services = new ServiceCollection()
                .AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings))
                .AddSingleton<ICanonicalCampaignContextResolver>(new CanonicalCampaignContextResolver(new BoundSessions(), paths, new FixedAvailability()))
                .AddSingleton<ICanonicalCampaignRootEnricher>(new CanonicalCampaignRootEnricher(paths, new TestOptionsMonitor<ArcanumSettings>(settings)))
                .AddSingleton(System.Reflection.DispatchProxy.Create<ICampaignRepository, UnreachedLongRestProxy>())
                .BuildServiceProvider();

            Result<CanonicalPingTurn> turn = await CanonicalPingResolution.ResolveTurnAsync(
                new("continue", SessionId: BoundSession, WorkingDirectory: "/inside"), new DefaultHttpContext { RequestServices = services }, CancellationToken.None);

            if (longRest)
            {
                Assert.True(turn.IsFailure);

                Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, turn.Error.Code);
            }
            else
            {
                Assert.True(turn.IsSuccess, turn.IsFailure ? turn.Error.Code : null);

                Assert.Equal(7, turn.Value.Campaign.PathIdentityRevision);

                Assert.Equal(0, paths.FindCalls);
            }
        }
    }

    private static ServiceProvider Boundary(ArcanumSettings settings, CountingPaths paths) => new ServiceCollection()
        .AddSingleton<IOptionsMonitor<ArcanumSettings>>(new TestOptionsMonitor<ArcanumSettings>(settings))
        .AddSingleton<ICanonicalCampaignContextResolver>(new CanonicalCampaignContextResolver(new BoundSessions(), paths, new FixedAvailability()))
        .AddSingleton<ICanonicalCampaignRootEnricher>(new CanonicalCampaignRootEnricher(paths, new TestOptionsMonitor<ArcanumSettings>(settings)))
        .AddSingleton(System.Reflection.DispatchProxy.Create<ICampaignRepository, UnreachedLongRestProxy>())
        .BuildServiceProvider();

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public async Task The_turn_boundary_enriches_exactly_the_requests_the_preparer_treats_as_Long_Rest_turns(bool longRest, bool statelessTranscript, bool noContext, bool enriched)
    {
        CountingPaths paths = new();

        ArcanumSettings settings = new();

        settings.Features.LongRest = longRest;

        settings.Features.CampaignRollups = true;

        using ServiceProvider services = Boundary(settings, paths);

        DefaultHttpContext http = new() { RequestServices = services };

        if (noContext)
        {
            http.Items[ArcanumInvocationContexts.NoContextRequestFeatureKey] = true;
        }

        PingRequest request = new("continue", SessionId: BoundSession, StatelessMessages: statelessTranscript ? [new("user", "continue")] : null);

        Result<CanonicalPingTurn> turn = await CanonicalPingResolution.ResolveTurnAsync(request, http, CancellationToken.None);

        Assert.True(turn.IsSuccess, turn.IsFailure ? turn.Error.Code : null);

        Assert.Equal(enriched ? 1 : 0, paths.FindCalls);

        Assert.Equal(enriched, turn.Value.Campaign.PathIdentityRevision is not null);

        // One rule, two places: the boundary enriches exactly when the turn it resolves is one the preparer prepares Long Rest for.
        ArcanumInvocationContext invocation = ArcanumInvocationContexts.ForTurn(http, turn.Value.Request, turn.Value.Campaign);

        Assert.Equal(enriched, settings.Features.LongRest && CampaignRollupTurnPreparer.IsLiveEligible(settings, turn.Value.Request, invocation));
    }

    [Theory]
    [InlineData("stateless-transcript")]
    [InlineData("no-context")]
    [InlineData("inspection")]
    public async Task A_root_that_moved_does_not_end_a_request_that_is_not_a_Long_Rest_turn_even_while_Long_Rest_is_on(string kind)
    {
        ArcanumSettings settings = new();

        settings.Features.LongRest = true;

        // The working directory resolved to the root at revision 7; the registry has since moved on to revision 8.
        RegisteredCampaignIdentity observed = new(BoundCampaign, 3, 7, 1, RegisteredDigest);

        CountingPaths paths = new() { Workspace = observed, Root = observed with { Revision = 8 } };

        using ServiceProvider services = Boundary(settings, paths);

        DefaultHttpContext http = new() { RequestServices = services };

        if (kind == "no-context")
        {
            http.Items[ArcanumInvocationContexts.NoContextRequestFeatureKey] = true;
        }

        PingRequest request = new("continue", SessionId: BoundSession, WorkingDirectory: "/inside",
            StatelessMessages: kind == "stateless-transcript" ? [new("user", "continue")] : null);

        Result<CanonicalPingTurn> turn = kind == "inspection"
            ? await CanonicalPingResolution.ResolveAsync(request, services, CancellationToken.None)
            : await CanonicalPingResolution.ResolveTurnAsync(request, http, CancellationToken.None);

        // The request keeps the resolver's answer, and the registry is never read for it.
        Assert.True(turn.IsSuccess, turn.IsFailure ? turn.Error.Code : null);

        Assert.Equal(7, turn.Value.Campaign.PathIdentityRevision);

        Assert.Equal(0, paths.FindCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_context_inspection_of_a_Session_bound_Campaign_revalidates_its_snapshot_whether_or_not_Long_Rest_is_on(bool longRest)
    {
        // The inspect endpoint resolves without the turn rule: inspection is not a Long Rest request, and its snapshot is built and revalidated
        // from the resolver's answer alone. A Session bound to a Campaign with a registered root, and no working directory supplied, is the
        // case where the Long Rest answer would differ from the resolver's.
        CountingPaths paths = new();

        ArcanumSettings settings = new();

        settings.Features.LongRest = longRest;

        settings.Features.CampaignRollups = true;

        using ServiceProvider services = Boundary(settings, paths);

        Result<CanonicalPingTurn> resolved = await CanonicalPingResolution.ResolveAsync(new("continue", SessionId: BoundSession), services, CancellationToken.None);

        Assert.True(resolved.IsSuccess, resolved.IsFailure ? resolved.Error.Code : null);

        ArcanumInvocationContext inspection = ArcanumInvocationContext.Create(ArcanumExecutionSurface.ContextInspection, resolved.Value.Campaign,
            InvocationAttendance.Unattended, CovenantContextPolicy.Default, ToolPolicy.NoTools, null).Value;

        CampaignRollupTurnPreparer preparer = new(new CampaignRollupTurnSnapshotTests.ProbeStore(),
            services.GetRequiredService<IOptionsMonitor<ArcanumSettings>>(), services.GetRequiredService<ICanonicalCampaignContextResolver>(), null!,
            UnreachedLongRestPorts.Coordinator, UnreachedLongRestPorts.Maintenance, UnreachedLongRestPorts.Discovery, UnreachedLongRestPorts.Configuration,
            services.GetRequiredService<ICanonicalCampaignRootEnricher>());

        await using CampaignRollupTurnSnapshot? snapshot = (await preparer.PreparePreviewAsync(resolved.Value.Request, inspection, CancellationToken.None)).Value;

        Assert.NotNull(snapshot);

        Result valid = await snapshot.ValidateAsync(CancellationToken.None);

        Assert.True(valid.IsSuccess, valid.IsFailure ? valid.Error.Code + ": " + valid.Error.Message : null);

        Assert.Equal(0, paths.FindCalls);
    }

    private sealed class BoundSessions : ISessionCampaignBindingReader
    {
        public ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(Guid? sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<SessionCampaignBindingRecord?>.Success(
                sessionId is { } id ? new SessionCampaignBindingRecord(id, SessionCampaignBinding.ForCampaign(BoundCampaign)) : null));
    }

    private sealed class FixedAvailability : ICampaignAvailabilityReader
    {
        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<long?>.Success(12));
    }

    private sealed class CountingPaths : ICampaignPathIdentityReader
    {
        public RegisteredCampaignIdentity? Workspace { get; init; }

        public RegisteredCampaignIdentity? Root { get; init; } = new(BoundCampaign, 3, 7, 1, RegisteredDigest);

        public int FindCalls { get; private set; }

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(string? workingDirectory, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(Workspace));

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(Guid campaignId, CancellationToken cancellationToken)
        {
            FindCalls++;

            return ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(Root));
        }
    }

    private static ServiceProvider Services(bool enabled)
    {
        ArcanumSettings settings = new();

        settings.Features.CampaignRollups = enabled;

        return new ServiceCollection().AddSingleton<IOptionsMonitor<ArcanumSettings>>(
            new TestOptionsMonitor<ArcanumSettings>(settings)).BuildServiceProvider();
    }

    private static CanonicalCampaignContext Campaign() => CanonicalCampaignContext.Create(
        SessionCampaignBinding.ForCampaign(Guid.NewGuid()), 1, 1, null, null);
}
