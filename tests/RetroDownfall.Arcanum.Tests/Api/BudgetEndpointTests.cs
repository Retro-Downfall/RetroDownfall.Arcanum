using System.Net;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class BudgetEndpointTests(ArcanumWebApplicationFactory factory)
{
    [Fact]
    public async Task Budget_management_route_is_absent_from_the_installed_API_surface()
    {
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync("/api/budget");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
