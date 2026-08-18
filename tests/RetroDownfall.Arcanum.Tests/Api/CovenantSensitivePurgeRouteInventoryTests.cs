using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.AspNetCore.TestHost;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Intelligence;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.TheForge;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// Issue #117 — the six routes that may delete a Covenant-labelled artifact all declare it.
/// </summary>
/// <remarks>
/// Route names rather than URLs, because a URL is a thing an operator types and a name is the thing the
/// contract is about. The list is exhaustive and pinned by hand: a seventh deletion route added without
/// the declaration would be a raw delete reachable over HTTP, and the only way to notice one is to write
/// the six down and fail when the set changes (§10.20.2).
///
/// <para>The declaration is what makes the pre-binding middleware issue a retention-purge authority for
/// the request. Without it a route can still call the purger, but on an installation with the Covenant
/// on the purger would find a label and no authority behind it, and refuse — so a missing declaration
/// fails closed rather than deleting protected state.</para>
/// </remarks>
public sealed class CovenantSensitivePurgeRouteInventoryTests
{

    /// <summary>Every route name that may reach a labelled artifact. Exhaustive on purpose.</summary>
    private static readonly string[] DeletionRoutes =
    [
        "DeleteSessionEntry",
        "CompactSession",
        "DeleteSagaMemory",
        "DeleteAllSagaMemories",
        "DeleteLexiconEntry",
        "EmbeddingsReset",
    ];

    [Theory]

    [InlineData("DeleteSessionEntry")]

    [InlineData("CompactSession")]

    [InlineData("DeleteSagaMemory")]

    [InlineData("DeleteAllSagaMemories")]

    [InlineData("DeleteLexiconEntry")]

    [InlineData("EmbeddingsReset")]

    public async Task Every_direct_deletion_route_declares_the_conditional_sensitivity_purge(string routeName)
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        Endpoint endpoint = graph.Endpoint(routeName);

        Assert.NotNull(
            endpoint.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>());

    }

    /// <summary>
    /// The inventory is the complete set, not a sample of it.
    /// </summary>
    /// <remarks>
    /// Fails in both directions. A new route that declares the conditional purge without being listed
    /// here is a deletion path nobody wrote down, and a listed route that stops declaring it is one that
    /// silently went back to a raw delete.
    /// </remarks>
    [Fact]
    public async Task The_declared_set_is_exactly_the_six_named_deletion_routes()
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] declared =
        [
            .. graph.Endpoints
                .Where(static endpoint =>
                    endpoint.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>() is not null)
                .Select(static endpoint =>
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. DeletionRoutes.Order(StringComparer.Ordinal)], declared);

    }

    private sealed class RouteGraph : IAsyncDisposable
    {

        private WebApplication _app = null!;

        internal IReadOnlyList<Endpoint> Endpoints =>
            _app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        internal static async Task<RouteGraph> CreateAsync()
        {

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

            builder.WebHost.UseTestServer();

            RouteGraph graph = new();

            graph._app = builder.Build();

            RouteGroupBuilder api = graph._app.MapGroup("/api");

            _ = api.MapSessionEndpoints();

            _ = api.MapSagaEndpoints();

            _ = api.MapMemoryEndpoints();

            _ = api.MapEmbeddingsResetEndpoints();

            await graph._app.StartAsync();

            return graph;

        }

        internal Endpoint Endpoint(string name) =>
            Assert.Single(
                Endpoints,
                endpoint => string.Equals(
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                    name,
                    StringComparison.Ordinal));

        public async ValueTask DisposeAsync()
        {

            await _app.StopAsync();

            await _app.DisposeAsync();

        }

    }

}
