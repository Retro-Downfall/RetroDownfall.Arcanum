using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// <c>POST /v1/moderations</c> — DESIGN.md §11.18. Uses the shared "ApiHost" factory (default
/// <c>Arcanum:Moderations:Enabled = false</c>) for the disabled-by-default assertions, and a
/// dedicated factory (matching the pattern in <c>WorkspacesEndpointTests.WriteEndpoints_ToggleDisabled_Return403Envelope</c>)
/// for the enabled pass-through assertions.
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
    public async Task PostModerations_DisabledByDefault_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent("""{"input":"hello world"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiErrorResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(body);

        Assert.Equal("feature_disabled", body.Error.Code);

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

    [SkippableFact]
    public async Task PostModerations_Enabled_WithStringInput_ReturnsUnflaggedPassThrough()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabledFactory = CreateEnabledFactory();

        HttpClient client = enabledFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent("""{"input":"hello world"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiModerationResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModerationResponse);

        Assert.NotNull(body);

        Assert.Equal("omni-moderation-latest", body.Model);

        OpenAiModerationResult result = Assert.Single(body.Results);

        Assert.False(result.Flagged);

        Assert.False(result.Categories.Sexual);

        Assert.False(result.Categories.SelfHarmInstructions);

        Assert.Equal(0.0, result.CategoryScores.Violence);

    }

    [SkippableFact]
    public async Task PostModerations_Enabled_WithArrayInput_ReturnsOneResultPerInput()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabledFactory = CreateEnabledFactory();

        HttpClient client = enabledFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent("""{"input":["alpha","beta","gamma"]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        OpenAiModerationResponse? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.OpenAiModerationResponse);

        Assert.NotNull(body);

        Assert.Equal(3, body.Results.Count);

        Assert.All(body.Results, static r => Assert.False(r.Flagged));

    }

    [SkippableFact]
    public async Task PostModerations_Enabled_MissingInput_Returns400()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory enabledFactory = CreateEnabledFactory();

        HttpClient client = enabledFactory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/moderations",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

    }

    private static ArcanumWebApplicationFactory CreateEnabledFactory() =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Moderations = settings.Moderations with { Enabled = true },
            },
        };

}
