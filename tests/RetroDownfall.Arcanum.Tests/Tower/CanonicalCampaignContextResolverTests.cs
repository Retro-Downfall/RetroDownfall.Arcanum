using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;

namespace RetroDownfall.Arcanum.Tests.Tower;

/// <summary>
/// The composed resolver: read order, failure propagation, and the answers it hands a turn.
/// </summary>
/// <remarks>
/// The truth table itself is proven against the pure policy. What is proven here is the composition:
/// that a supplied Session is checked before any filesystem work, that a reader failure is propagated
/// rather than swallowed, and that availability is read for the Campaign the table actually resolved.
/// </remarks>
public sealed class CanonicalCampaignContextResolverTests
{

    private static readonly Guid CampaignC = Guid.Parse("7E1D9C42-05B8-4F63-9A0E-3C8B27D641F5");

    private static readonly Guid CampaignD = Guid.Parse("1A2B3C4D-5E6F-4071-8293-A4B5C6D7E8F9");

    private static readonly Guid SessionId = Guid.Parse("3F2E1D0C-9B8A-4756-8493-A2B1C0D9E8F7");

    private static CancellationToken Token => CancellationToken.None;

    [Fact]
    public async Task An_unknown_session_fails_without_touching_the_filesystem_or_creating_a_session()
    {

        StubPaths paths = new();

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings { Failure = new Error(ErrorCodes.Session.NotFound, "Session not found.") },
            paths,
            new StubAvailability());

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(SessionId, null, "/somewhere"),
            Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Session.NotFound, result.Error.Code);
        Assert.Equal(0, paths.ResolveCalls);

    }

    [Fact]
    public async Task A_session_bound_campaign_is_carried_with_its_availability_generation()
    {

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings { Binding = SessionCampaignBinding.ForCampaign(CampaignC) },
            new StubPaths(),
            new StubAvailability { Generation = 12 });

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(SessionId, null, null),
            Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(CampaignC, result.Value.CampaignId);
        Assert.Equal(12, result.Value.CampaignAvailabilityGeneration);

    }

    [Fact]
    public async Task A_deleted_campaign_between_binding_and_availability_fails_closed()
    {

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings { Binding = SessionCampaignBinding.ForCampaign(CampaignC) },
            new StubPaths(),
            new StubAvailability { Generation = null });

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(SessionId, null, null),
            Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Campaign.NotFound, result.Error.Code);

    }

    [Fact]
    public async Task A_supplied_path_inside_a_different_campaign_conflicts()
    {

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings { Binding = SessionCampaignBinding.ForCampaign(CampaignC) },
            new StubPaths { Workspace = Registered(CampaignD) },
            new StubAvailability { Generation = 12 });

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(SessionId, null, "/elsewhere"),
            Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.CampaignBindingConflict, result.Error.Code);

    }

    [Fact]
    public async Task A_request_with_no_session_and_no_sources_resolves_global_only()
    {

        StubAvailability availability = new();

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings(),
            new StubPaths(),
            availability);

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(null, null, null),
            Token);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsCampaignBound);

        // No Campaign was resolved, so no availability read happened at all.
        Assert.Equal(0, availability.Calls);

    }

    [Fact]
    public async Task A_path_reader_failure_is_propagated_rather_than_treated_as_unregistered()
    {

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings(),
            new StubPaths { Failure = new Error(ErrorCodes.Covenant.IntegrityFailure, "malformed") },
            new StubAvailability());

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(null, null, "/somewhere"),
            Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);

    }

    [Fact]
    public async Task A_legacy_unresolved_session_never_reaches_availability()
    {

        StubAvailability availability = new();

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings { Binding = SessionCampaignBinding.LegacyUnresolved },
            new StubPaths(),
            availability);

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(SessionId, CampaignC, null),
            Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.Session.CampaignBindingRequired, result.Error.Code);
        Assert.Equal(0, availability.Calls);

    }

    [Fact]
    public async Task The_resolver_answers_the_table_and_never_reads_the_registered_root_of_the_campaign_it_resolved()
    {

        // The Campaign is named, no directory is supplied, and its root is registered: the root is not part of this answer.
        StubPaths paths = new() { Root = Registered(CampaignC) };

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings(),
            paths,
            new StubAvailability { Generation = 12 });

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(null, CampaignC, null),
            Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, paths.FindCalls);

        // Byte-identical to the pure policy's answer: a policy version and nothing of the root.
        Assert.Equal(CanonicalCampaignResolutionPolicy.Resolve(null, CampaignC, null, false, 12).Value, result.Value);
        Assert.Equal(CampaignPathIdentityPolicy.Version, result.Value.PathIdentityPolicyVersion);
        Assert.Null(result.Value.PathIdentityRevision);
        Assert.Null(result.Value.RootIdentityDigest);

    }

    [Fact]
    public async Task A_workspace_the_resolver_observed_is_carried_as_observed_whatever_the_registry_says_later()
    {

        RegisteredCampaignIdentity workspace = Registered(CampaignC);

        // The registry now holds a newer revision of the same root. Telling the two apart is the enricher's job, not the resolver's.
        StubPaths paths = new() { Workspace = workspace, Root = workspace with { Revision = 9 } };

        CanonicalCampaignContextResolver resolver = new(
            new StubBindings(),
            paths,
            new StubAvailability { Generation = 12 });

        Result<CanonicalCampaignContext> result = await resolver.ResolveAsync(
            new CanonicalCampaignResolutionRequest(null, CampaignC, "/inside"),
            Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, paths.FindCalls);
        Assert.Equal(CanonicalCampaignResolutionPolicy.Resolve(null, CampaignC, workspace, true, 12).Value, result.Value);
        Assert.Equal(1, result.Value.PathIdentityRevision);

    }

    private static RegisteredCampaignIdentity Registered(Guid campaignId) =>
        new(
            campaignId,
            CampaignPathIdentityPolicy.Version,
            Revision: 1,
            Depth: 2,
            new CovenantDigest(System.Security.Cryptography.SHA256.HashData(campaignId.ToByteArray())));

    private sealed class StubBindings : ISessionCampaignBindingReader
    {

        public SessionCampaignBinding? Binding { get; init; }

        public Error? Failure { get; init; }

        public ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(
            Guid? sessionId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                Failure is { } error
                    ? Result<SessionCampaignBindingRecord?>.Failure(error)
                    : Result<SessionCampaignBindingRecord?>.Success(
                        sessionId is null || Binding is not { } binding
                            ? null
                            : new SessionCampaignBindingRecord(sessionId.Value, binding)));

    }

    private sealed class StubPaths : ICampaignPathIdentityReader
    {

        public RegisteredCampaignIdentity? Workspace { get; init; }

        /// <summary>The root the registry holds for a Campaign; the observed <see cref="Workspace"/> when a test sets only that.</summary>
        public RegisteredCampaignIdentity? Root { get; init; }

        public Error? Failure { get; init; }

        public int ResolveCalls { get; private set; }

        public int FindCalls { get; private set; }

        public ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(
            string? workingDirectory,
            CancellationToken cancellationToken)
        {

            ResolveCalls++;

            return ValueTask.FromResult(
                Failure is { } error
                    ? Result<RegisteredCampaignIdentity?>.Failure(error)
                    : Result<RegisteredCampaignIdentity?>.Success(Workspace));

        }

        public ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(
            Guid campaignId,
            CancellationToken cancellationToken)
        {

            FindCalls++;

            return ValueTask.FromResult(Result<RegisteredCampaignIdentity?>.Success(Root ?? Workspace));

        }

    }

    private sealed class StubAvailability : ICampaignAvailabilityReader
    {

        public long? Generation { get; init; } = 1;

        public int Calls { get; private set; }

        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(
            Guid campaignId,
            CancellationToken cancellationToken)
        {

            Calls++;

            return ValueTask.FromResult(Result<long?>.Success(Generation));

        }

    }

}
