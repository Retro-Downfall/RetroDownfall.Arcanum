using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /v1/images/*</c> and <c>POST /v1/audio/*</c> — unconditional <c>501</c> stubs
/// (DESIGN.md §11.19). No config toggle: these routes always return "not implemented" regardless
/// of settings, unlike <c>/v1/moderations</c>.
/// </summary>
[Collection("ApiHost")]
public sealed class OpenAiV1UnsupportedStubsEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public OpenAiV1UnsupportedStubsEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableTheory]
    [InlineData("/v1/images/generations")]
    [InlineData("/v1/images/edits")]
    [InlineData("/v1/images/variations")]
    [InlineData("/v1/audio/transcriptions")]
    [InlineData("/v1/audio/translations")]
    [InlineData("/v1/audio/speech")]
    public async Task PostUnsupportedRoute_Returns501NotSupportedEnvelope(string route)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            route,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("not_supported", body.Error.Code);

        Assert.Equal("invalid_request_error", body.Error.Type);

        Assert.Null(body.Error.Param);

    }

    [SkippableTheory]
    [InlineData("/v1/images/generations")]
    [InlineData("/v1/audio/speech")]
    public async Task PostUnsupportedRoute_WithoutApiKey_Returns401(string route)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            route,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

}
