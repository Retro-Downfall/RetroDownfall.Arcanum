using System.Net;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class HealthEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public HealthEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetHealth_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<string>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Auth.Unauthorized", body.Error?.Code);

    }

    [SkippableFact]
    public async Task GetHealth_WithValidApiKey_ReturnsComponentHealthPayload()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<HealthReportDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseHealthReportDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotEmpty(body.Data.Components);

        Assert.Contains(body.Data.Components, static c => c.Name == "Grimoire");

        Assert.Contains(body.Data.Components, static c => c.Name == "MCP");

        Assert.Contains(body.Data.Components, static c => c.Name == "Providers");

    }

    [SkippableFact]
    public async Task GetHealth_WithWrongApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, "wrong-key");

        HttpResponseMessage response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

}
