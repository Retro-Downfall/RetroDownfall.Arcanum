using System.Net;
using System.Text;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class MemoryReviewEndpointTests(ArcanumWebApplicationFactory factory)
{
    [Theory]
    [InlineData(
        "/api/memory/lexicon/review/list",
        """
        {"scope":null,"limit":10,"cursor":null}
        """)]
    [InlineData(
        "/api/memory/lexicon/review/apply",
        """
        {"request":null,"preparedPlanToken":"prepared"}
        """)]
    [InlineData(
        "/api/memory/saga/review/apply",
        """
        {"request":null,"preparedPlanToken":"prepared"}
        """)]
    [InlineData(
        "/api/memory/covenant/review/apply",
        """
        {"request":null,"preparedPlanToken":"prepared"}
        """)]
    public async Task Review_endpoints_reject_incomplete_nested_bodies_without_throwing(
        string route,
        string json)
    {
        using HttpClient client = factory.CreateAuthenticatedClient();

        using StringContent body = new(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await client.PostAsync(route, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string responseBody = await response.Content.ReadAsStringAsync();

        Assert.Contains("Validation.InvalidBody", responseBody, StringComparison.Ordinal);
    }
}
