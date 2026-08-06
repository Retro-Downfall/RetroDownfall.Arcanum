using System.Net;

using RetroDownfall.Arcanum.Api.A2A;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// Route-mapping behavior for the A2A server surface. Exercises the real host so the feature gates,
/// <see cref="RetroDownfall.Arcanum.Api.Security.ApiKeyEndpointFilter"/>, and the Agent Card endpoint
/// all run through production code.
/// </summary>
/// <remarks>
/// Issue #12 removed the Development-edition gate that previously hid the A2A server from every
/// non-Development host. The feature gates (<c>Arcanum:Features:Conclave</c> plus
/// <c>Arcanum:Features:A2AServer</c>) remain the only opt-in, and authentication is still required.
/// </remarks>
[Collection("ApiHost")]
public sealed class A2AServerMappingTests : IDisposable
{

    private const string AgentCardPath = "/api/conclave/a2a/agent-card";

    private readonly string? _originalEdition =
        global::System.Environment.GetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar);

    public A2AServerMappingTests()
    {

        global::System.Environment.SetEnvironmentVariable(ArcanumEnvironment.EditionEnvVar, null);

    }

    [SkippableTheory]
    [InlineData(ArcanumEdition.Local)]
    [InlineData(ArcanumEdition.Development)]
    public async Task AgentCard_IsServedWhenA2AServerIsEnabled_RegardlessOfEdition(ArcanumEdition edition)
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactory(edition, a2aServer: true);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(AgentCardPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"name\"", json, StringComparison.Ordinal);

    }

    [SkippableFact]
    public async Task AgentCard_IsNotMapped_WhenA2AServerFeatureIsOff()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactory(ArcanumEdition.Development, a2aServer: false);

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(AgentCardPath);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task AgentCard_RequiresApiKey_EvenWhenMapped()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactory(ArcanumEdition.Local, a2aServer: true);

        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(AgentCardPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

    }

    [Theory]
    [InlineData("/api/conclave/a2a", "/api/conclave/a2a")]
    [InlineData("  /api/conclave/a2a  ", "/api/conclave/a2a")]
    [InlineData("/api", "/api")]
    // A path outside /api used to silently disable the whole server. It is now mounted under /api so
    // the operator gets the surface they asked for, still behind ApiKeyEndpointFilter (issue #12).
    [InlineData("/conclave/a2a", "/api/conclave/a2a")]
    [InlineData("/agents/inbound", "/api/agents/inbound")]
    [InlineData("agents/inbound", "/api/agents/inbound")]
    [InlineData("/", "/api")]
    [InlineData("", "/api/conclave/a2a")]
    [InlineData("   ", "/api/conclave/a2a")]
    public void ResolveServerPath_MountsEveryConfiguredPathUnderTheAuthenticatedApiGroup(string configured, string expected)
    {

        Assert.Equal(expected, A2AServerEndpoints.ResolveServerPath(configured));

    }

    [SkippableFact]
    public async Task AgentCard_IsServedAtAConfiguredPathOutsideApi()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Edition = ArcanumEdition.Local,
                Features = (settings.Features ?? new FeatureSettings()) with
                {
                    Conclave = true,
                    A2AServer = true,
                },
                Integrations = (settings.Integrations ?? new IntegrationSettings()) with
                {
                    A2A = new A2AIntegrationSettings { ServerPath = "/conclave/inbound" },
                },
            },
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/conclave/inbound/agent-card");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string json = await response.Content.ReadAsStringAsync();

        // The Agent Card must advertise the path it actually mounted at, not the raw configured value.
        Assert.Contains("/api/conclave/inbound", json, StringComparison.Ordinal);

    }

    private static ArcanumWebApplicationFactory CreateFactory(ArcanumEdition edition, bool a2aServer) =>
        new()
        {
            SettingsOverride = settings => settings with
            {
                Edition = edition,
                Features = (settings.Features ?? new FeatureSettings()) with
                {
                    Conclave = true,
                    A2AServer = a2aServer,
                },
            },
        };

    public void Dispose()
    {

        global::System.Environment.SetEnvironmentVariable(
            ArcanumEnvironment.EditionEnvVar,
            _originalEdition);

    }

}
