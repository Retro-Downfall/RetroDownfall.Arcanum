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
