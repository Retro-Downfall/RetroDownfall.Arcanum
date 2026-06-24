using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ApiWireContractTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ApiWireContractTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task PostConfigValidate_MalformedJson_ReturnsEnveloped400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/config/validate",
            new StringContent("{not-json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.NotNull(body.Error);

        Assert.Equal("Validation.InvalidBody", body.Error!.Value.Code);

    }

    [SkippableFact]
    public async Task PostSessions_MissingBody_ReturnsEnveloped400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.NotNull(body.Error);

        Assert.Equal("Validation.InvalidBody", body.Error!.Value.Code);

    }

    [SkippableFact]
    public async Task PostLore_MalformedJson_ReturnsEnveloped400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/lore",
            new StringContent("{bad", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<LoreDto>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseLoreDto);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.NotNull(body.Error);

        Assert.Equal("Validation.InvalidBody", body.Error!.Value.Code);

    }

    [SkippableFact]
    public async Task GetPerceptionLook_WithAllowedRoot_ReturnsOkEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        string encoded = Uri.EscapeDataString(_factory.TempHome);

        HttpResponseMessage response = await client.GetAsync($"/api/perception/look?directory={encoded}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<PatternSnapshot>? body = JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponsePatternSnapshot);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

    }

    [SkippableFact]
    public async Task GetEventsLogs_WithAvailableGate_ReturnsEventStream()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/api/events/logs",
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.StartsWith("text/event-stream", response.Content.Headers.ContentType?.MediaType);

    }

}
