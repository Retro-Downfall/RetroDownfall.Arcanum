using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Intelligence;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class MemoryReviewRouteTests
{
    private static readonly string[] ReviewRoutes =
    [
        "ListSagaMemoryReviewQueue",
        "PrepareSagaMemoryReview",
        "ApplySagaMemoryReview",
        "ListLexiconMemoryReviewQueue",
        "PrepareLexiconMemoryReview",
        "ApplyLexiconMemoryReview",
        "ListCovenantMemoryReviewQueue",
        "PrepareCovenantMemoryReview",
        "ApplyCovenantMemoryReview",
    ];

    [Fact]
    public async Task Each_store_exposes_only_list_prepare_and_apply_review_routes()
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        string[] declared =
        [
            .. graph.Endpoints
                .Select(static endpoint =>
                    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? string.Empty)
                .Where(static name => name.Contains("MemoryReview", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal([.. ReviewRoutes.Order(StringComparer.Ordinal)], declared);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Review_selectors_are_typed_POST_bodies_and_never_URL_segments(string routeName)
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        RouteEndpoint endpoint = Assert.IsType<RouteEndpoint>(graph.Endpoint(routeName));

        Assert.Equal(["POST"], endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        Assert.DoesNotContain('{', endpoint.RoutePattern.RawText ?? string.Empty);
    }

    [Fact]
    public async Task Covenant_review_requires_protected_read_for_list_and_manage_for_both_bulk_phases()
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        Assert.Equal(
            CovenantAuthorityRequirement.ProtectedRead,
            graph.Endpoint("ListCovenantMemoryReviewQueue")
                .Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>()?.Requirement);

        foreach (string routeName in new[] { "PrepareCovenantMemoryReview", "ApplyCovenantMemoryReview" })
        {
            Assert.Equal(
                CovenantAuthorityRequirement.CovenantManage,
                graph.Endpoint(routeName)
                    .Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>()?.Requirement);
        }
    }

    [Fact]
    public async Task Lexicon_review_declares_conditional_read_and_exact_write_only_on_apply()
    {
        await using RouteGraph graph = await RouteGraph.CreateAsync();

        foreach (string routeName in new[] { "ListLexiconMemoryReviewQueue", "PrepareLexiconMemoryReview" })
        {
            Endpoint endpoint = graph.Endpoint(routeName);

            Assert.NotNull(endpoint.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>());

            Assert.Null(endpoint.Metadata.GetMetadata<CovenantConditionalExactWriteRequirementMetadata>());
        }

        Endpoint apply = graph.Endpoint("ApplyLexiconMemoryReview");

        Assert.NotNull(apply.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>());

        Assert.NotNull(apply.Metadata.GetMetadata<CovenantConditionalExactWriteRequirementMetadata>());
    }

    public static TheoryData<string> Routes() => new()
    {
        "ListSagaMemoryReviewQueue",
        "PrepareSagaMemoryReview",
        "ApplySagaMemoryReview",
        "ListLexiconMemoryReviewQueue",
        "PrepareLexiconMemoryReview",
        "ApplyLexiconMemoryReview",
        "ListCovenantMemoryReviewQueue",
        "PrepareCovenantMemoryReview",
        "ApplyCovenantMemoryReview",
    };

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

            _ = graph._app.MapGroup("/api").MapMemoryEndpoints();

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
