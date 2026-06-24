using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ApprenticeEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ApprenticeEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostApprentices_WithValidBody_ReturnsCreatedApprentice()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateApprenticeRequest request = new(
            Name: "Integration Apprentice",
            Goal: "Verify apprentice endpoints",
            WorkspacePath: _factory.TempHome);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateApprenticeRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/apprentices",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal("Integration Apprentice", body.Data.Name);

        Assert.Equal("Verify apprentice endpoints", body.Data.Goal);

        HttpResponseMessage getResponse = await client.GetAsync($"/api/apprentices/{body.Data.Id:D}");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        HttpResponseMessage deleteResponse = await client.DeleteAsync($"/api/apprentices/{body.Data.Id:D}");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

    }

    [SkippableFact]
    public async Task PostApprentices_EmptyName_ReturnsBadRequest()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateApprenticeRequest request = new(
            Name: "   ",
            Goal: "goal",
            WorkspacePath: _factory.TempHome);

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.CreateApprenticeRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/apprentices",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Apprentice.InvalidName", body.Error?.Code);

    }

    [SkippableFact]
    public async Task GetApprentice_MissingId_ReturnsNotFound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/apprentices/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ApprenticeDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Apprentice.NotFound", body.Error?.Code);

    }

    [SkippableFact]
    public async Task PostApprenticeStart_MissingId_ReturnsNotFound()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            $"/api/apprentices/{Guid.NewGuid():D}/start",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task PostApprenticeReweave_EmptyPlan_ReturnsBadRequest()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        CreateApprenticeRequest create = new(
            Name: "Reweave target",
            Goal: "Plan validation",
            WorkspacePath: _factory.TempHome);

        string createPayload = JsonSerializer.Serialize(create, ArcanumJsonContext.Default.CreateApprenticeRequest);

        HttpResponseMessage createResponse = await client.PostAsync(
            "/api/apprentices",
            new StringContent(createPayload, Encoding.UTF8, "application/json"));

        ApiResponse<ApprenticeDetailDto>? created = JsonSerializer.Deserialize(
            await createResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseApprenticeDetailDto);

        Assert.NotNull(created?.Data);

        ReweaveApprenticeRequest reweave = new([]);

        string reweavePayload = JsonSerializer.Serialize(reweave, ArcanumJsonContext.Default.ReweaveApprenticeRequest);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/apprentices/{created.Data.Id:D}/reweave",
            new StringContent(reweavePayload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await client.DeleteAsync($"/api/apprentices/{created.Data.Id:D}");

    }

}
