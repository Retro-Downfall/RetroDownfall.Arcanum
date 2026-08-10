using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
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
    public async Task PostCommLinkSend_NonJsonContentType_ReturnsEnveloped415NotHubUnhandled()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // curl's default content type. ReadFromJsonAsync raises InvalidOperationException here, which used
        // to escape as HTTP 500 Hub.Unhandled with an Error-level stack trace for a routine client mistake.
        HttpResponseMessage response = await client.PostAsync(
            "/api/commlink/send",
            new StringContent(
                """{"title":"t","body":"b"}""",
                Encoding.UTF8,
                "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("Hub.Unhandled", json, StringComparison.Ordinal);

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.NotNull(body.Error);

        Assert.Equal("Validation.UnsupportedMediaType", body.Error!.Value.Code);

    }

    [SkippableFact]
    public async Task PutConfig_NonJsonContentType_ReturnsEnveloped415()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // Round-trip the live snapshot so the body is a well-formed ArcanumSettings tree: the config write
        // route used to parse it straight off Request.Body, so the media-type gate never ran at all.
        HttpResponseMessage snapshotResponse = await client.GetAsync("/api/config");

        ApiResponse<ArcanumSettings>? snapshot = JsonSerializer.Deserialize(
            await snapshotResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseArcanumSettings);

        Assert.NotNull(snapshot?.Data);

        string payload = JsonSerializer.Serialize(
            snapshot.Data,
            ArcanumJsonContext.Default.ArcanumSettings);

        HttpResponseMessage response = await client.PutAsync(
            "/api/config",
            new StringContent(payload, Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.NotNull(body.Error);

        Assert.Equal("Validation.UnsupportedMediaType", body.Error!.Value.Code);

    }

    [SkippableFact]
    public async Task PostConfigValidate_NonJsonContentType_ReturnsEnveloped415()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/api/config/validate",
            new StringContent(
                """{"providers":[]}""",
                Encoding.UTF8,
                "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<bool>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseBoolean);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.NotNull(body.Error);

        Assert.Equal("Validation.UnsupportedMediaType", body.Error!.Value.Code);

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
