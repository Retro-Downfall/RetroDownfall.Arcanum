using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Api;
using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Api.Intelligence.OpenAi;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.DataLifecycle;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Infrastructure.A2A;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class InstallationResetApiAdmissionTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public InstallationResetApiAdmissionTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [Fact]
    public void Production_routes_mark_only_health_quit_and_factory_replay_for_recovery_admission()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        string[] admittedNames =
        [
            "GetHealth",
            "QuitServer",
            "FactoryResetDataRetention",
        ];

        foreach (string endpointName in admittedNames)
        {

            Endpoint endpoint = Assert.Single(endpoints.Endpoints, candidate =>
                string.Equals(
                    candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                    endpointName,
                    StringComparison.Ordinal));

            Assert.NotNull(
                endpoint.Metadata.GetMetadata<InstallationResetRecoveryApiRouteMetadata>());

        }

        string[] blockedNames =
        [
            "PlanFactoryResetDataRetention",
            "PostPerceptionChronosync",
            "PostOpenAiChatCompletions",
        ];

        foreach (string endpointName in blockedNames)
        {

            Endpoint endpoint = Assert.Single(endpoints.Endpoints, candidate =>
                string.Equals(
                    candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                    endpointName,
                    StringComparison.Ordinal));

            Assert.Null(
                endpoint.Metadata.GetMetadata<InstallationResetRecoveryApiRouteMetadata>());

        }

    }

    [Fact]
    public void Every_mapped_application_route_is_covered_by_auth_recovery_or_hidden_recovery_admission()
    {

        _ = _factory.CreateAuthenticatedClient();

        EndpointDataSource endpoints = _factory.Services.GetRequiredService<EndpointDataSource>();

        foreach (RouteEndpoint endpoint in endpoints.Endpoints.OfType<RouteEndpoint>())
        {

            bool authenticated = endpoint.Metadata.GetMetadata<ApiKeyRequirementMetadata>() is not null;

            bool hidden = endpoint.Metadata
                .GetMetadata<InstallationResetRecoveryHiddenRouteMetadata>() is not null;

            bool blocked = endpoint.Metadata
                .GetMetadata<InstallationResetRecoveryBlockedRouteMetadata>() is not null;

            Assert.True(
                authenticated || hidden || blocked,
                $"Route '{endpoint.RoutePattern.RawText}' has no recovery admission boundary.");

        }

    }

    [SkippableFact]
    public async Task Production_pipeline_authenticates_first_then_blocks_recovery_ineligible_routes_with_typed_conflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        using ArcanumWebApplicationFactory factory = new();

        _ = factory.CreateAuthenticatedClient();

        InstallationResetApiAdmission admission = factory.Services
            .GetRequiredService<InstallationResetApiAdmission>();

        admission.PublishRecovery(CreateActive(
            "pipeline-plan",
            Guid.Parse("56565656-5656-4656-8656-565656565656")));

        using HttpClient unauthenticated = factory.CreateClient();

        using HttpResponseMessage unauthenticatedResponse = await unauthenticated.PostAsync(
            "/api/perception/chronosync",
            content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);

        using HttpClient authenticated = factory.CreateAuthenticatedClient();

        using HttpResponseMessage blocked = await authenticated.PostAsync(
            "/api/perception/chronosync",
            content: null);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        ApiResponse<string>? body = JsonSerializer.Deserialize(
            await blocked.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseString);

        Assert.NotNull(body);

        Assert.False(body.IsSuccess);

        Assert.Equal(ErrorCodes.Data.ResetInProgress, body.Error?.Code);

        using HttpResponseMessage openAiBlocked = await authenticated.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.Conflict, openAiBlocked.StatusCode);

        OpenAiErrorResponse? openAiBody = JsonSerializer.Deserialize(
            await openAiBlocked.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.OpenAiErrorResponse);

        Assert.NotNull(openAiBody);

        Assert.Equal("installation_reset_in_progress", openAiBody.Error.Code);

    }

    [Fact]
    public async Task Recovery_mode_hides_the_peer_callback_before_registry_or_ledger_access()
    {

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        InstallationResetApiAdmission admission = new();

        admission.PublishRecovery(CreateActive(
            "callback-plan",
            Guid.Parse("58585858-5858-4858-8858-585858585858")));

        builder.Services.AddSingleton(admission);

        WebApplication app = builder.Build();

        app.UseArcanumApiKeyAuthentication();

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings
            {
                Conclave = true,
                A2AServer = true,
            },
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    PushNotifications = true,
                },
            },
        };

        app.MapA2ACallbacks(settings, rateLimiterPolicyName: null);

        await app.StartAsync();

        try
        {

            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.PostAsync(
                "/api/conclave/a2a/callbacks/nonexistent",
                content: null);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        }
        finally
        {

            await app.DisposeAsync();

        }

    }

    [Fact]
    public async Task Api_composition_without_recovery_state_service_remains_a_normal_authenticated_host()
    {

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();

        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton<ISecretStore>(
            new TestApiKeySecretStore(ArcanumWebApplicationFactory.TestApiKey));

        builder.Services.AddSingleton<IApiKeyDigestCache, ApiKeyDigestCache>();

        builder.Services.AddSingleton<ApiKeyAuthenticator>();

        WebApplication app = builder.Build();

        app.UseArcanumApiKeyAuthentication();

        app.MapGroup("/api")
            .RequireArcanumApiKey()
            .MapGet("/probe", () => Results.Ok());

        await app.StartAsync();

        try
        {

            using HttpClient client = app.GetTestClient();

            client.DefaultRequestHeaders.Add(
                ArcanumApiHeaders.ApiKey,
                ArcanumWebApplicationFactory.TestApiKey);

            using HttpResponseMessage response = await client.GetAsync("/api/probe");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        }
        finally
        {

            await app.DisposeAsync();

        }

    }

    [Fact]
    public void Recovery_gate_precedes_covenant_authority_and_parameter_binding_in_the_auth_middleware()
    {

        string source = Assert.Single(
            ProductionSourceInventory.Sources(),
            static candidate => candidate.IsExactOwner(
                "src/RetroDownfall.Arcanum.Api/ApiBootstrapper.cs")).Text;

        int recovery = source.IndexOf(
            "ApplyInstallationResetRecoveryAdmissionAsync(context)",
            StringComparison.Ordinal);

        int covenant = source.IndexOf(
            "ApplyCovenantPreBindingPolicyAsync(context)",
            StringComparison.Ordinal);

        Assert.True(recovery >= 0, "the pre-binding recovery admission call is missing");

        Assert.True(
            covenant > recovery,
            "Covenant authority was issued before recovery-mode admission");

    }

    private static ActiveInstallationReset CreateActive(string planId, Guid operationId) =>
        new(
            InstallationResetScope.Global,
            WorkspaceRoot: null,
            planId,
            operationId,
            InstallationResetPhase.Prepared,
            InstallationResetDataHandoff.HostFactoryErasure,
            OnlineDataCompletionDurable: false);

}
