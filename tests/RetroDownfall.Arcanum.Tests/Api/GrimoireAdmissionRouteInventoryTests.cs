using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Which composed routes stand outside Grimoire admission, and where admission sits in the pipeline.
/// </summary>
/// <remarks>
/// Both halves are inventories rather than examples. An exemption that can be added by attaching a
/// marker is one a later route can quietly join; an ordering that lives inside one middleware
/// delegate is one a later edit can quietly reorder. Neither would fail any behavioural test that
/// happened not to exercise the new route or the new order.
/// </remarks>
[Collection("ApiHost")]
public sealed class GrimoireAdmissionRouteInventoryTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public GrimoireAdmissionRouteInventoryTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    /// <summary>
    /// Exactly two composed routes are exempt, and both are exempt for a stated reason.
    /// </summary>
    /// <remarks>
    /// Health because it already answers a closed gate better than a refusal could — its own probe
    /// catches the refusal and reports an Unhealthy component naming only an exception type, inside
    /// the documented success envelope that <c>arcanum doctor</c>, <c>arcanum watch health</c> and
    /// auto-launch all parse. Quit because it is the shutdown step of the factory-reset sequence and
    /// opens nothing; refusing it strands a reset between its host-apply proof and its offline
    /// continuation.
    ///
    /// <para>The factory-reset route is deliberately absent even though it shares the recovery-host
    /// marker with these two. It is the request maintenance runs for, and it has to hold a lease so
    /// the erasure can promote it out of its own drain.</para>
    /// </remarks>
    [Fact]
    public void Exactly_health_and_quit_stand_outside_grimoire_admission()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        string[] exempt =
        [
            .. endpoints.Endpoints
                .Where(static candidate =>
                    candidate.Metadata.GetMetadata<GrimoireAdmissionExemptRouteMetadata>() is not null)
                .Select(static candidate =>
                    candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                    ?? candidate.DisplayName
                    ?? "<unnamed>")
                .OrderBy(static name => name, StringComparer.Ordinal),
        ];

        Assert.Equal(["GetHealth", "QuitServer"], exempt);

    }

    /// <summary>
    /// The composed host registers the holder admission resolves, in the request scope.
    /// </summary>
    /// <remarks>
    /// Admission answers a missing holder by admitting, the way every other pre-binding stage answers
    /// a service a bare host does not compose. That is right for a host with no Grimoire and would be
    /// silent for a real one that lost the registration, so the registration is asserted here rather
    /// than defended by a throw in the request path.
    /// </remarks>
    [Fact]
    public void The_composed_host_registers_the_admission_holder_per_request()
    {

        _ = _factory.CreateAuthenticatedClient();

        using IServiceScope first = _factory.Services.CreateScope();

        using IServiceScope second = _factory.Services.CreateScope();

        GrimoireRequestAdmissionScope one =
            first.ServiceProvider.GetRequiredService<GrimoireRequestAdmissionScope>();

        Assert.Same(one, first.ServiceProvider.GetRequiredService<GrimoireRequestAdmissionScope>());

        Assert.NotSame(
            one,
            second.ServiceProvider.GetRequiredService<GrimoireRequestAdmissionScope>());

        Assert.Null(one.Lease);

    }

    /// <summary>
    /// The factory-reset route is admitted like any other request, not exempted.
    /// </summary>
    [Fact]
    public void The_factory_reset_route_is_not_exempt_from_admission()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        Endpoint factoryReset = Assert.Single(
            endpoints.Endpoints,
            static candidate => string.Equals(
                candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                "FactoryResetDataRetention",
                StringComparison.Ordinal));

        Assert.Null(factoryReset.Metadata.GetMetadata<GrimoireAdmissionExemptRouteMetadata>());

        Assert.NotNull(
            factoryReset.Metadata.GetMetadata<InstallationResetRecoveryApiRouteMetadata>());

    }

    /// <summary>
    /// In the authenticated branch, admission decides before every later pre-binding stage.
    /// </summary>
    /// <remarks>
    /// The search starts at the authenticator call rather than at the top of the file, because the
    /// same admission helper is also called in the anonymous branch above it. An index taken from the
    /// start of the file would be satisfied by that earlier call no matter where the authenticated
    /// one was moved to, which is precisely the reordering this test exists to catch.
    /// </remarks>
    [Fact]
    public void Grimoire_admission_precedes_recovery_admission_and_covenant_authority_after_the_key_check()
    {

        string source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs")).Text;

        int authenticated = source.IndexOf(
            "authenticator.IsAuthorizedAsync(context)",
            StringComparison.Ordinal);

        Assert.True(authenticated >= 0, "the API-key check is missing");

        int admission = source.IndexOf(
            "ApplyGrimoireRequestAdmissionAsync(context)",
            authenticated,
            StringComparison.Ordinal);

        int recovery = source.IndexOf(
            "ApplyInstallationResetRecoveryAdmissionAsync(context)",
            authenticated,
            StringComparison.Ordinal);

        int covenant = source.IndexOf(
            "ApplyCovenantPreBindingPolicyAsync(context)",
            authenticated,
            StringComparison.Ordinal);

        Assert.True(admission >= 0, "the authenticated branch does not take Grimoire admission");

        Assert.True(recovery > admission, "recovery admission ran before Grimoire admission");

        Assert.True(covenant > admission, "Covenant authority was issued before Grimoire admission");

    }

    /// <summary>
    /// The anonymous branch takes admission too, and only after the route it must keep hidden is hidden.
    /// </summary>
    /// <remarks>
    /// An <c>/api</c>-rooted route can carry no API-key metadata at all — the peer callback is one —
    /// and path authority is what protects it. But the hide has to win: a route that must stay
    /// indistinguishably unavailable during an installation reset cannot start answering
    /// "this host exists and its database is under maintenance" instead.
    /// </remarks>
    [Fact]
    public void The_anonymous_branch_hides_before_it_admits()
    {

        string source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs")).Text;

        int hide = source.IndexOf(
            "HideRecoveryIneligibleAnonymousRouteAsync(context)",
            StringComparison.Ordinal);

        int admission = source.IndexOf(
            "ApplyGrimoireRequestAdmissionAsync(context)",
            StringComparison.Ordinal);

        int authenticated = source.IndexOf(
            "authenticator.IsAuthorizedAsync(context)",
            StringComparison.Ordinal);

        Assert.True(hide >= 0, "the anonymous hide is missing");

        Assert.True(admission > hide, "admission ran before the anonymous hide");

        Assert.True(
            admission < authenticated,
            "the anonymous branch does not take Grimoire admission");

    }

}
