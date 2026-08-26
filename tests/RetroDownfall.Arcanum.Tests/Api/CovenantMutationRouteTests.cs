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
/// The routes an operator writes the Covenant through, and the authority each declares.
/// </summary>
/// <remarks>
/// Names rather than URLs: a URL is what an operator types, a name is what the contract is about.
/// The set is exhaustive on purpose and fails in both directions — a Covenant mutation route added
/// without being written down here is a write path nobody recorded.
/// </remarks>
public sealed class CovenantMutationRouteTests
{

    private static readonly string[] MutationRoutes =
    [
        "PrepareCovenantSet",
        "PrepareCovenantRetire",
        "SetCovenantEntry",
        "RetireCovenantEntry",
        "PrepareCovenantCuration",
        "CurateCovenantEntry",
        "PrepareCovenantCorrection",
        "CorrectCovenantEntry",
    ];

    [Theory]

    [InlineData("PrepareCovenantSet")]

    [InlineData("PrepareCovenantRetire")]

    [InlineData("SetCovenantEntry")]

    [InlineData("RetireCovenantEntry")]

    [InlineData("PrepareCovenantCuration")]

    [InlineData("CurateCovenantEntry")]

    [InlineData("PrepareCovenantCorrection")]

    [InlineData("CorrectCovenantEntry")]

    public async Task Every_covenant_mutation_route_requires_operator_manage_authority(string routeName)
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        Endpoint endpoint = graph.Endpoint(routeName);

        CovenantAuthorityRequirementMetadata? metadata =
            endpoint.Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>();

        Assert.NotNull(metadata);

        // The pre-binding middleware reads this before a body byte is bound. A route that carried
        // ProtectedRead here would let a read authority commit a write.
        Assert.Equal(CovenantAuthorityRequirement.CovenantManage, metadata.Requirement);

    }

    [Fact]
    public async Task The_declared_set_is_exactly_the_named_mutation_routes()
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] declared =
        [
            .. graph.Endpoints
                .Where(static endpoint =>
                    endpoint.Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>()
                        is { Requirement: CovenantAuthorityRequirement.CovenantManage })
                .Select(static endpoint =>
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. MutationRoutes.Order(StringComparer.Ordinal)], declared);

    }

    [Fact]
    public async Task A_commit_route_answers_PUT_and_a_prepare_route_answers_POST()
    {

        await using RouteGraph graph = await RouteGraph.CreateAsync();

        // The set commit is a PUT because it is an idempotent assertion of one key's content, and its
        // preparation is a POST because it mints a token rather than asserting anything.
        Assert.Contains(
            "PUT",
            graph.Endpoint("SetCovenantEntry").Metadata
                .GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        Assert.Contains(
            "POST",
            graph.Endpoint("PrepareCovenantSet").Metadata
                .GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

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

            _ = graph._app.MapGroup("/api").MapCovenantMutationEndpoints();

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
