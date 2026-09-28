using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LexiconCurationRouteInventoryTests
{
    [Fact]
    public async Task Exact_curation_declares_six_static_POST_names_with_conditional_read_and_five_exact_write_markers()
    {
        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        Endpoint[] endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.ToArray();

        string[] names = ["ShowLexiconEntry", "CorrectLexiconEntry", "RetireLexiconEntry", "ReinstateLexiconEntry", "PinLexiconEntry", "UnpinLexiconEntry"];

        string[] verbs = ["show", "correct", "retire", "reinstate", "pin", "unpin"];

        for (int index = 0; index < names.Length; index++)
        {
            RouteEndpoint route = Assert.IsType<RouteEndpoint>(Assert.Single(endpoints,
                endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == names[index]));

            Assert.Equal("/api/memory/lexicon/" + verbs[index], route.RoutePattern.RawText);

            Assert.Equal(["POST"], route.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

            Assert.Empty(route.RoutePattern.Parameters);

            Assert.NotNull(route.Metadata.GetMetadata<CovenantConditionalReadRequirementMetadata>());

            Assert.Equal(index != 0, route.Metadata.Any(item => item.GetType().Name == "CovenantConditionalExactWriteRequirementMetadata"));

            Assert.Null(route.Metadata.GetMetadata<CovenantAuthorityRequirementMetadata>());

            Assert.Null(route.Metadata.GetMetadata<CovenantConditionalSensitivityPurgeMetadata>());
        }

        Assert.Equal(names.Skip(1).Append("ApplyLexiconMemoryReview").Order(StringComparer.Ordinal), endpoints
            .Where(endpoint => endpoint.Metadata.Any(item => item.GetType().Name == "CovenantConditionalExactWriteRequirementMetadata"))
            .Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()!.EndpointName).Order(StringComparer.Ordinal));
    }
}
