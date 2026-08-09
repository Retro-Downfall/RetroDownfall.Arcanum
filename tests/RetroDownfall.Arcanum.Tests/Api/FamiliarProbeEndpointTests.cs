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
using RetroDownfall.Arcanum.Infrastructure.Familiars;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

/// <summary>
/// The probe runs host-side because Compendium is an editor and must not grow a second
/// process-spawn implementation of its own. That makes this endpoint the contract between them, so
/// these facts pin its shape, its auth, and the two ways it can legitimately refuse.
/// </summary>
[Collection("ApiHost")]
public sealed class FamiliarProbeEndpointTests
{

    private static readonly ProviderSettings Familiar = new()
    {
        Name = "ClaudeCode-subscription",
        Type = AiProviderKind.ClaudeCodeCli,
        HiddenModels = ["claude-legacy"],
    };

    private static readonly ProviderSettings HttpProvider = new()
    {
        Name = "compat",
        Type = AiProviderKind.OpenAICompatible,
        Endpoint = "https://api.openai.com/v1",
        Models = ["gpt-4o"],
    };

    [SkippableFact]
    public async Task A_familiar_provider_reports_its_status()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        FamiliarProbeResult probed = new(
            Familiar.Name,
            nameof(AiProviderKind.ClaudeCodeCli),
            FamiliarProbeStatus.Configured,
            "2.1.220",
            "Claude Code is installed and signed in (max).",
            string.Empty,
            null,
            FamiliarModelEnumeration.Unknown,
            [],
            ["claude-legacy"]);

        ApiResponse<FamiliarProbeResult>? body = await GetProbeAsync(
            "ClaudeCode-subscription",
            probed,
            HttpStatusCode.OK);

        Assert.True(body!.IsSuccess);

        Assert.Equal(FamiliarProbeStatus.Configured, body.Data!.Status);

        Assert.Equal("2.1.220", body.Data.Version);

        Assert.Equal(["claude-legacy"], body.Data.HiddenModels);

    }

    [SkippableFact]
    public async Task An_unknown_provider_is_a_404_rather_than_an_empty_status()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApiResponse<FamiliarProbeResult>? body = await GetProbeAsync(
            "no-such-provider",
            probeResult: null,
            HttpStatusCode.NotFound);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Provider.NotFound, body.Error!.Value.Code);

    }

    /// <summary>
    /// An HTTP provider has no Familiar to ask, and saying so beats returning a status that implies
    /// Arcanum probed something it never could.
    /// </summary>
    [SkippableFact]
    public async Task An_http_provider_is_rejected_rather_than_probed()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApiResponse<FamiliarProbeResult>? body = await GetProbeAsync(
            "compat",
            probeResult: null,
            HttpStatusCode.BadRequest);

        Assert.False(body!.IsSuccess);

        Assert.Equal(ErrorCodes.Validation.InvalidProviderType, body.Error!.Value.Code);

    }

    [SkippableFact]
    public async Task The_probe_requires_the_api_key_like_every_other_api_route()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        await using WebApplicationFactory<Program> scoped = Configure(factory, probeResult: null);

        HttpClient client = scoped.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/providers/ClaudeCode-subscription/familiar-probe");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    private static async Task<ApiResponse<FamiliarProbeResult>?> GetProbeAsync(
        string providerName,
        FamiliarProbeResult? probeResult,
        HttpStatusCode expected)
    {

        await using ArcanumWebApplicationFactory factory = new();

        await using WebApplicationFactory<Program> scoped = Configure(factory, probeResult);

        HttpClient client = scoped.CreateClient();

        client.DefaultRequestHeaders.Add(ArcanumApiHeaders.ApiKey, ArcanumWebApplicationFactory.TestApiKey);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/providers/{Uri.EscapeDataString(providerName)}/familiar-probe");

        Assert.Equal(expected, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize(
            json,
            ArcanumJsonContext.Default.ApiResponseFamiliarProbeResult);

    }

    private static WebApplicationFactory<Program> Configure(
        ArcanumWebApplicationFactory factory,
        FamiliarProbeResult? probeResult) =>
        factory.WithWebHostBuilder(builder =>
        {

            builder.ConfigureTestServices(services =>
            {

                services.RemoveAll<IFamiliarProbe>();

                services.AddSingleton<IFamiliarProbe>(new StubFamiliarProbe(probeResult));

                services.RemoveAll<IOptionsMonitor<ArcanumSettings>>();

                services.AddSingleton<IOptionsMonitor<ArcanumSettings>>(sp =>
                {

                    ArcanumSettings built = sp
                        .GetRequiredService<IOptionsFactory<ArcanumSettings>>()
                        .Create(Options.DefaultName);

                    ArcanumSettings patched = built with
                    {

                        Providers = [Familiar, HttpProvider],

                        Security = built.Security with
                        {
                            SpellWorkspaceRoots = [factory.TempHome],
                            CampaignRoots = [factory.TempHome],
                        },

                        Workspaces = built.Workspaces with { DefaultRoot = factory.TempHome },

                    };

                    return new TestOptionsMonitor<ArcanumSettings>(patched);

                });

                services.RemoveAll<IOptions<ArcanumSettings>>();

                services.AddSingleton<IOptions<ArcanumSettings>>(sp =>
                    Options.Create(sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

                services.RemoveAll<IOptionsSnapshot<ArcanumSettings>>();

                services.AddSingleton<IOptionsSnapshot<ArcanumSettings>>(sp =>
                    new TestOptionsSnapshot<ArcanumSettings>(
                        sp.GetRequiredService<IOptionsMonitor<ArcanumSettings>>().CurrentValue));

            });

        });

    private sealed class StubFamiliarProbe(FamiliarProbeResult? result) : IFamiliarProbe
    {

        public Task<FamiliarProbeResult> ProbeAsync(
            ProviderSettings provider,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                result
                ?? throw new InvalidOperationException("The probe should not have been reached."));

    }

}
