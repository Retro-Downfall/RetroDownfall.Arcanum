using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class ModelsProvidersEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public ModelsProvidersEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetModels_returns_all_configured_models()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ModelInfoDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray);

        Assert.NotNull(body?.Data);

        Assert.True(body!.IsSuccess);

        Assert.Contains(body.Data!, m => m.Model == "mistral:latest" && m.ProviderName == "test");

    }

    [SkippableFact]
    public async Task GetModels_redacts_endpoint()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ModelInfoDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray);

        Assert.NotNull(body?.Data);

        Assert.All(body!.Data!, m => Assert.Equal("***", m.Endpoint));

    }

    [SkippableFact]
    public async Task GetModels_ReportsSupportsVisionFromModelEntry()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ProviderSettings visionProvider = new()
        {
            Name = "vision-provider",
            Type = AiProviderKind.OpenAICompatible,
            Endpoint = "https://example.test/v1",
            Models = [new ModelEntry("vision-model", SupportsVision: true), new ModelEntry("text-model")],
        };

        await using ArcanumWebApplicationFactory isolatedFactory = new();

        await using WebApplicationFactory<Program> scoped = isolatedFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {

                services.RemoveAll<IOptionsMonitor<ArcanumSettings>>();

                services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(sp =>
                {

                    ArcanumSettings built = sp.GetRequiredService<IOptionsFactory<ArcanumSettings>>().Create(Options.DefaultName);

                    ArcanumSettings patched = built with
                    {
                        DefaultModel = "vision-model",
                        Providers = [visionProvider],
                        Spells = built.Spells with { AllowedWorkspaceRoots = [isolatedFactory.TempHome] },
                        Campaigns = built.Campaigns with { AllowedRoots = [isolatedFactory.TempHome] },
                        Host = built.Host with { Workspace = isolatedFactory.TempHome },
                    };

                    return new TestOptionsMonitor<ArcanumSettings>(patched);

                });

                services.RemoveAll<IOptions<ArcanumSettings>>();

                services.AddSingleton<IOptions<ArcanumSettings>>(sp =>
                    Options.Create(sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

                services.RemoveAll<IOptionsSnapshot<ArcanumSettings>>();

                services.AddSingleton<IOptionsSnapshot<ArcanumSettings>>(sp =>
                    new TestOptionsSnapshot<ArcanumSettings>(sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

            });
        });

        HttpClient client = scoped.CreateClient();

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

        HttpResponseMessage response = await client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ModelInfoDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseModelInfoDtoArray);

        Assert.NotNull(body?.Data);

        Assert.True(body!.Data!.Single(m => m.Model == "vision-model").SupportsVision);

        Assert.False(body.Data!.Single(m => m.Model == "text-model").SupportsVision);

    }

    [SkippableFact]
    public async Task GetProviders_returns_provider_list()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ProviderInfoDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseProviderInfoDtoArray);

        Assert.NotNull(body?.Data);

        Assert.Contains(body!.Data!, p => p.Name == "test");

    }

    [SkippableFact]
    public async Task GetProviders_redacts_apikey()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        ApiResponse<ProviderInfoDto[]>? body = JsonSerializer.Deserialize(json, ArcanumJsonContext.Default.ApiResponseProviderInfoDtoArray);

        Assert.NotNull(body?.Data);

        Assert.All(body!.Data!, p => Assert.Equal("***", p.Endpoint));

    }

}
