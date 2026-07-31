using System.Net;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ConfigEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ConfigEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetConfig_WithValidApiKey_ReturnsRedactedSettingsEnvelope()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ArcanumSettings>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseArcanumSettings);

        Assert.NotNull(body);

        Assert.True(body.IsSuccess);

        Assert.NotNull(body.Data);

        Assert.NotNull(body.Data.Host);

    }

    [SkippableFact]
    public async Task GetConfig_WithoutApiKey_Returns401()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [SkippableFact]

    public async Task PostConfigValidate_MergesRedactedSnapshotBeforeOutboundValidation()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage getResponse = await client.GetAsync("/api/config");

        string getJson = await getResponse.Content.ReadAsStringAsync();

        ApiResponse<ArcanumSettings>? envelope = JsonSerializer.Deserialize(
            getJson,
            ArcanumJsonContext.Default.ApiResponseArcanumSettings);

        Assert.NotNull(envelope?.Data);

        Assert.Contains(
            envelope.Data.Providers,
            static provider => provider.Endpoint == "***");

        string payload = JsonSerializer.Serialize(
            envelope.Data,
            ArcanumJsonContext.Default.ArcanumSettings);

        HttpResponseMessage validationResponse = await client.PostAsync(
            "/api/config/validate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        string validationJson = await validationResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);

        Assert.Contains("Security.BlockedOutboundUrl", validationJson, StringComparison.Ordinal);

        Assert.DoesNotContain("Config.UnresolvedMask", validationJson, StringComparison.Ordinal);

        Assert.DoesNotContain("***", validationJson, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task PostConfigValidate_WithLlamaCppServerType_ReturnsMigrationValidationFailed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string payload =
            """
            {
              "providers": [
                {
                  "name": "local",
                  "type": "LlamaCppServer",
                  "models": ["mistral"]
                }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/api/config/validate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        Assert.Contains("Configuration.ValidationFailed", json, StringComparison.Ordinal);

        Assert.Contains("LlamaCppServer", json, StringComparison.Ordinal);

        Assert.Contains("OpenAICompatible", json, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task PostConfigValidate_WithRootLlamaCppKey_ReturnsMigrationValidationFailed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string payload =
            """
            {
              "llamaCpp": { "serverExecutablePath": "/tmp/llama" },
              "providers": [
                {
                  "name": "local",
                  "type": "OpenAICompatible",
                  "endpoint": "http://localhost:11434/v1",
                  "models": ["mistral"]
                }
              ]
            }
            """;

        HttpResponseMessage response = await client.PostAsync(
            "/api/config/validate",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        Assert.Contains("Configuration.ValidationFailed", json, StringComparison.Ordinal);

        Assert.Contains("llamaCpp", json, StringComparison.OrdinalIgnoreCase);

    }

    [SkippableFact]
    public async Task PutConfig_WithLlamaCppServerType_ReturnsBadRequestWithoutWriting()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string payload =
            """
            {
              "providers": [
                {
                  "name": "local",
                  "type": "LlamaCppServer",
                  "models": ["mistral"]
                }
              ]
            }
            """;

        HttpResponseMessage putResponse = await client.PutAsync(
            "/api/config",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, putResponse.StatusCode);

        string putJson = await putResponse.Content.ReadAsStringAsync();

        Assert.Contains("Configuration.ValidationFailed", putJson, StringComparison.Ordinal);

        Assert.Contains("LlamaCppServer", putJson, StringComparison.Ordinal);

        // Confirm GET still succeeds (obsolete PUT did not take the host down / corrupt config write path).
        HttpResponseMessage after = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);

    }

}
