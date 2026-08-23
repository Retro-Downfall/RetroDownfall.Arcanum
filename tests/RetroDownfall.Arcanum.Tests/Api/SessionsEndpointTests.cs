using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SessionsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SessionsEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetSessionAnalytics_WithValidApiKey_ReturnsAnalyticsEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/sessions/analytics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SessionAnalytics>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSessionAnalytics);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task PostSessions_WithValidBody_ReturnsCreatedSessionEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateSessionRequest request = new(CampaignId: null, Title: "integration session");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateSessionRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal("integration session", body.Data.Title);

        Assert.NotEqual(Guid.Empty, body.Data.Id);

        HttpResponseMessage getResponse = await client.GetAsync($"/api/sessions/{body.Data.Id:D}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        string getJson = await getResponse.Content.ReadAsStringAsync();

        ApiResponse<SessionDetailDto>? getBody = JsonSerializer.Deserialize(
            getJson,
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(getBody);

        Assert.True(getBody.IsSuccess);

        Assert.NotNull(getBody.Data);

        Assert.Equal(body.Data.Id, getBody.Data.Id);

    }

}
