using System.Net;

using System.Text;

using System.Text.Json;

using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Security;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Infrastructure.Security;

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

    public async Task GetConfig_AfterRetentionUpdate_ReturnsLivePersistedRule()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateLiveConfigurationFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage update = await client.PutAsync(
            "/api/data/retention",
            new StringContent(
                """
                {"dataClass":"archived-sessions","enabled":true,"days":73}
                """,
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        HttpResponseMessage response = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<ArcanumSettings>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseArcanumSettings);

        Assert.NotNull(body?.Data);

        Assert.True(body.Data.Retention.ArchivedSessions.Enabled);

        Assert.Equal(73, body.Data.Retention.ArchivedSessions.Days);

    }

    [SkippableFact]

    public async Task PutConfig_RoundTripAfterRetentionUpdate_DoesNotRestoreStartupRule()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateLiveConfigurationFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage update = await client.PutAsync(
            "/api/data/retention",
            new StringContent(
                """
                {"dataClass":"archived-sessions","enabled":true,"days":73}
                """,
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        HttpResponseMessage get = await client.GetAsync("/api/config");

        ApiResponse<ArcanumSettings>? snapshot = JsonSerializer.Deserialize(
            await get.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseArcanumSettings);

        Assert.NotNull(snapshot?.Data);

        string payload = JsonSerializer.Serialize(
            snapshot.Data,
            ArcanumJsonContext.Default.ArcanumSettings);

        HttpResponseMessage put = await client.PutAsync(
            "/api/config",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        HttpResponseMessage retained = await client.GetAsync(
            "/api/data/retention");

        ApiResponse<RetentionSettings>? policy = JsonSerializer.Deserialize(
            await retained.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseRetentionSettings);

        Assert.NotNull(policy?.Data);

        Assert.True(policy.Data.ArchivedSessions.Enabled);

        Assert.Equal(73, policy.Data.ArchivedSessions.Days);

    }

    [SkippableFact]

    public async Task PutConfig_SerializesValidationAndWriteWithConcurrentRetentionUpdate()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateLiveConfigurationFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        ApiResponse<ArcanumSettings>? snapshot = await ReadConfigAsync(client);

        Assert.NotNull(snapshot?.Data);

        ProviderSettings provider = Assert.Single(snapshot.Data.Providers);

        ArcanumSettings replacement = snapshot.Data with
        {

            DefaultModel = "race-model",

            Providers =
            [
                provider with
                {

                    Endpoint = "https://config-race.invalid/v1",

                    Models = ["race-model"],

                },
            ],

        };

        IDnsResolver originalResolver = OutboundUrlGuard.DnsResolver;

        BlockingDnsResolver resolver = new(
            "config-race.invalid",
            originalResolver);

        OutboundUrlGuard.DnsResolver = resolver;

        try
        {

            string payload = JsonSerializer.Serialize(
                replacement,
                ArcanumJsonContext.Default.ArcanumSettings);

            Task<HttpResponseMessage> fullUpdate = client.PutAsync(
                "/api/config",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            await resolver.WaitUntilBlockedAsync();

            Task<HttpResponseMessage> retentionUpdate = client.PutAsync(
                "/api/data/retention",
                new StringContent(
                    "{\"dataClass\":\"archived-sessions\",\"enabled\":true,\"days\":73}",
                    Encoding.UTF8,
                    "application/json"));

            _ = await Task.WhenAny(
                retentionUpdate,
                Task.Delay(TimeSpan.FromMilliseconds(250)));

            resolver.Release();

            using HttpResponseMessage fullResponse = await fullUpdate;

            using HttpResponseMessage retentionResponse = await retentionUpdate;

            Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);

            Assert.Equal(HttpStatusCode.OK, retentionResponse.StatusCode);

            ApiResponse<ArcanumSettings>? current = await ReadConfigAsync(client);

            Assert.Equal("race-model", current?.Data?.DefaultModel);

            Assert.True(current?.Data?.Retention.ArchivedSessions.Enabled);

            Assert.Equal(73, current?.Data?.Retention.ArchivedSessions.Days);

        }
        finally
        {

            resolver.Release();

            OutboundUrlGuard.DnsResolver = originalResolver;

        }

    }

    [SkippableFact]

    public async Task ConfigAndModelDiscoveryReadsShareTheLatestPersistedSnapshot()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateLiveConfigurationFactory();

        using HttpClient client = factory.CreateAuthenticatedClient();

        ApiResponse<ArcanumSettings>? snapshot = await ReadConfigAsync(client);

        Assert.NotNull(snapshot?.Data);

        ProviderSettings provider = Assert.Single(snapshot.Data.Providers);

        ArcanumSettings replacement = snapshot.Data with
        {

            DefaultModel = "qwen:latest",

            Providers =
            [
                provider with
                {

                    Models = ["qwen:latest"],

                },
            ],

        };

        using HttpResponseMessage put = await client.PutAsync(
            "/api/config",
            new StringContent(
                JsonSerializer.Serialize(
                    replacement,
                    ArcanumJsonContext.Default.ArcanumSettings),
                Encoding.UTF8,
                "application/json"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        ApiResponse<ArcanumSettings>? config = await ReadConfigAsync(client);

        using HttpResponseMessage modelsResponse = await client.GetAsync("/api/models");

        using HttpResponseMessage providersResponse = await client.GetAsync("/api/providers");

        using HttpResponseMessage openAiModelsResponse = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, modelsResponse.StatusCode);

        Assert.Equal(HttpStatusCode.OK, providersResponse.StatusCode);

        Assert.Equal(HttpStatusCode.OK, openAiModelsResponse.StatusCode);

        ApiResponse<ModelInfoDto[]>? models = JsonSerializer.Deserialize(
            await modelsResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray);

        ApiResponse<ProviderInfoDto[]>? providers = JsonSerializer.Deserialize(
            await providersResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseProviderInfoDtoArray);

        OpenAiModelListResponse? openAiModels = JsonSerializer.Deserialize(
            await openAiModelsResponse.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiModelListResponse);

        Assert.Equal("qwen:latest", config?.Data?.DefaultModel);

        ModelInfoDto nativeModel = Assert.Single(models?.Data ?? []);

        Assert.Equal("qwen:latest", nativeModel.Model);

        ProviderInfoDto discoveredProvider = Assert.Single(providers?.Data ?? []);

        Assert.Equal(["qwen:latest"], discoveredProvider.Models);

        Assert.Equal(
            "qwen:latest",
            Assert.Single(openAiModels?.Data ?? []).Id);

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
    public async Task PostConfigValidate_WithSemanticValidationFailure_ReturnsBadRequest()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        // Blank provider name plus an empty model list clears the raw-tree gate and fails only in
        // ConfigurationValidator.Validate — the branch that used to answer 200 with isSuccess:false, so
        // status-code-driven scripts (curl -f, raise_for_status) treated invalid config as validated.
        const string payload =
            """
            {
              "providers": [
                {
                  "name": "",
                  "type": "OpenAICompatible",
                  "models": []
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

    }

    [SkippableFact]
    public async Task PostConfigValidate_WithValidSettings_ReturnsOk()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        const string payload =
            """
            {
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Contains(
            "\"isSuccess\":true",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

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

    private static ArcanumWebApplicationFactory CreateLiveConfigurationFactory() =>
        new()
        {

            SettingsOverride = settings => settings with
            {

                DefaultModel = "mistral:latest",

                Providers =
                [
                    new ProviderSettings
                    {

                        Name = "local",

                        Type = AiProviderKind.OpenAICompatible,

                        Endpoint = "http://localhost:11434/v1",

                        Models = ["mistral:latest"],

                    },
                ],

            },

        };

    private static async Task<ApiResponse<ArcanumSettings>?> ReadConfigAsync(
        HttpClient client)
    {

        using HttpResponseMessage response = await client.GetAsync("/api/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseArcanumSettings);

    }

    private sealed class BlockingDnsResolver(
        string blockedHost,
        IDnsResolver fallback) : IDnsResolver
    {

        private readonly TaskCompletionSource<bool> _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IPAddress[]> GetHostAddressesAsync(
            string host,
            CancellationToken cancellationToken = default)
        {

            if (!string.Equals(host, blockedHost, StringComparison.OrdinalIgnoreCase))
            {

                return await fallback
                    .GetHostAddressesAsync(host, cancellationToken)
                    .ConfigureAwait(false);

            }

            _entered.TrySetResult(true);

            await _released.Task
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return [IPAddress.Parse("203.0.113.43")];

        }

        public Task WaitUntilBlockedAsync() => _entered.Task;

        public void Release() => _released.TrySetResult(true);

    }

}
