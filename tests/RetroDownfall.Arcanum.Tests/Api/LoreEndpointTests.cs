using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class LoreEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public LoreEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetLore_WithValidApiKey_ReturnsPagedEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/lore");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ListPageResult<LoreDto>>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseListPageResultLoreDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.Items);

    }

    [SkippableFact]
    public async Task PostLore_WithValidBody_ReturnsSavedLoreEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string key = $"integration-{Guid.NewGuid():N}";

        UpsertLoreRequest request = new(key, "integration test value");

        string payload = JsonSerializer.Serialize(request, ArcanumJsonContext.Default.UpsertLoreRequest);

        HttpResponseMessage response = await client.PostAsync(
            "/api/lore",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LoreDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseLoreDto);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.Equal(key, body.Data.Key);

        Assert.Equal("integration test value", body.Data.Value);

    }

    [SkippableFact]
    public async Task PostLore_MissingKeyAndValue_Returns400Envelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/lore",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LoreDto>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseLoreDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal("Validation.InvalidLore", body.Error?.Code);

    }

}
