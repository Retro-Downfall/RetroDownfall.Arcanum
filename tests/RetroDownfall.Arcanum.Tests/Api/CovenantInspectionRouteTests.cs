using Microsoft.AspNetCore.Builder;

using Microsoft.AspNetCore.Http;

using Microsoft.AspNetCore.Routing;

using Microsoft.AspNetCore.TestHost;

using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Security;

using RetroDownfall.Arcanum.Api.Tower;

using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The six routes an operator reads their own Covenant through.
/// </summary>
public sealed class CovenantInspectionRouteTests
{

    private static readonly string[] InspectionRoutes =
    [
        "ListCovenantEntries",
        "QueryCovenantEntries",
        "ShowCovenantEntry",
        "ListCovenantVersions",
        "ListCovenantSources",
        "ExplainCovenant",
    ];

    [Theory]

    [InlineData("ListCovenantEntries")]

    [InlineData("QueryCovenantEntries")]

    [InlineData("ShowCovenantEntry")]

    [InlineData("ListCovenantVersions")]

    [InlineData("ListCovenantSources")]

    [InlineData("ExplainCovenant")]

    public async Task Every_inspection_route_requires_protected_read_authority(string routeName)
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        CovenantAuthorityRequirementMetadata? metadata = graph.Endpoint(routeName)
            .Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>();

        Assert.NotNull(metadata);

        Assert.Equal(CovenantAuthorityRequirement.ProtectedRead, metadata.Requirement);

    }

    [Fact]
    public async Task The_declared_set_is_exactly_the_six_named_inspection_routes()
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] declared =
        [
            .. graph.Endpoints
                .Select(static endpoint =>
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. InspectionRoutes.Order(StringComparer.Ordinal)], declared);

    }

    /// <summary>
    /// Nothing an operator inspects travels in a URL.
    /// </summary>
    /// <remarks>
    /// Every inspection route is a POST with a typed body, including the ones that only read. Scope
    /// selections, Campaign identities, keys, free text, and cursors are all either protected content
    /// or a direct pointer to it, and a URL is the one part of a request that reliably reaches an
    /// access log.
    /// </remarks>
    [Fact]
    public async Task No_inspection_route_carries_its_selector_in_the_url()
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        foreach (string routeName in InspectionRoutes)
        {

            Endpoint endpoint = graph.Endpoint(routeName);

            Assert.Equal(["POST"], endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

            Assert.DoesNotContain('{', ((RouteEndpoint)endpoint).RoutePattern.RawText ?? string.Empty);

        }

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

            _ = graph._app.MapGroup("/api").MapCovenantInspectionEndpoints();

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
