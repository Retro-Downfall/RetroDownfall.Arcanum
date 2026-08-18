using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /v1/moderations</c> — always <c>501 not_supported</c> (no fake moderation verdicts).
/// </summary>
[Collection("ApiHost")]
public sealed class OpenAiV1ModerationsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public OpenAiV1ModerationsEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task PostModerations_Always_Returns501NotSupported()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent("""{"input":"hello world"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("not_supported", body.Error.Code);
    }

    /// <summary>
    /// The handler discards its bound body (<c>_ = body;</c>), but binding still ran, so a payload that
    /// would not deserialize answered the framework's 400/415 rather than the route's own unconditional
    /// 501. "Not supported" cannot depend on the shape of a body nothing reads.
    /// </summary>
    [SkippableTheory]
    [InlineData("not json at all", "application/json")]
    [InlineData("""{"input":"hello"}""", "text/plain")]
    [InlineData("""{"input":{"nested":true}}""", "application/json")]
    public async Task PostModerations_Returns501_WhateverTheBodyLooksLike(string payload, string contentType)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent(payload, Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);
        Assert.Equal("not_supported", body.Error.Code);
    }

    [SkippableFact]
    public async Task PostModerations_WithoutApiKey_Returns401()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent("""{"input":"hello"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

}
