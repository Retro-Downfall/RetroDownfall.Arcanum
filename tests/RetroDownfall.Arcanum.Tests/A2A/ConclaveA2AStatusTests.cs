using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;

namespace RetroDownfall.Arcanum.Tests.A2A;

/// <summary>
/// The shared Conclave/A2A status projection behind <c>GET /api/health</c>, <c>GET /api/meta</c>, and the
/// CLI. Issue #12 requires these surfaces to distinguish disabled, configured, degraded, and healthy.
/// </summary>
public sealed class ConclaveA2AStatusTests
{

    private static readonly string UsableRoot = Directory.GetCurrentDirectory();

    [Fact]
    public void Resolve_NothingEnabledAndNothingConfigured_IsDisabled()
    {

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(new ArcanumSettings());

        Assert.Equal(ConclaveA2AState.Disabled, status.State);

        Assert.False(status.ConclaveEnabled);

        Assert.False(status.ServerEnabled);

        Assert.False(status.ClientEnabled);

        Assert.Null(status.ServerPath);

        Assert.Contains("disabled", status.Detail, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Resolve_SettingsPresentButNoFeatureGate_IsConfigured()
    {

        ArcanumSettings settings = new()
        {
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    AgentCardName = "My Arcanum",
                    AllowedRemoteAgents = ["https://partner.example.test/"],
                },
            },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal(ConclaveA2AState.Configured, status.State);

        Assert.False(status.ServerEnabled);

        Assert.False(status.ClientEnabled);

        // The next action must be named, not guessed at.
        Assert.Contains("Arcanum:Features:A2AServer", status.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public void Resolve_ServerEnabledWithUsableWorkspace_IsHealthyAndReportsEffectivePaths()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { A2AServer = true },
            Security = new SecuritySettings { CampaignRoots = [UsableRoot] },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal(ConclaveA2AState.Healthy, status.State);

        Assert.True(status.ConclaveEnabled);

        Assert.True(status.ServerEnabled);

        Assert.Equal("/api/conclave/a2a", status.ServerPath);

        Assert.Equal("/api/conclave/a2a/agent-card", status.AgentCardPath);

    }

    [Fact]
    public void Resolve_ServerEnabledWithoutAnyAllowedCampaignRoot_IsDegradedAndNamesTheOwner()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { A2AServer = true },
            Security = new SecuritySettings { CampaignRoots = [] },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal(ConclaveA2AState.Degraded, status.State);

        Assert.Contains("Arcanum:Security:CampaignRoots", status.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public void Resolve_ServerPathOutsideApi_ReportsTheEffectiveMountedPath()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { A2AServer = true },
            Security = new SecuritySettings { CampaignRoots = [UsableRoot] },
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings { ServerPath = "/conclave/inbound" },
            },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal("/api/conclave/inbound", status.ServerPath);

        Assert.Equal("/api/conclave/inbound/agent-card", status.AgentCardPath);

    }

    [Fact]
    public void Resolve_ClientOnly_IsHealthyWithoutAServerPath()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { A2AClient = true },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal(ConclaveA2AState.Healthy, status.State);

        Assert.True(status.ClientEnabled);

        Assert.False(status.ServerEnabled);

        Assert.Null(status.ServerPath);

    }

    [Fact]
    public void Resolve_ReportsAllowlistSizeButNeverTheEntriesThemselves()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { A2AClient = true },
            Integrations = new IntegrationSettings
            {
                A2A = new A2AIntegrationSettings
                {
                    AllowedRemoteAgents = ["https://private-partner.internal.test/", "https://other.example.test/"],
                },
            },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal(2, status.AllowedRemoteAgentCount);

        // Allowlist entries can name private endpoints; the operator-facing detail must not echo them.
        Assert.DoesNotContain("private-partner.internal.test", status.Detail, StringComparison.OrdinalIgnoreCase);

    }

    [Fact]
    public void Resolve_ConclaveWithoutA2A_IsHealthyForLocalDelegationOnly()
    {

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Conclave = true },
        };

        ConclaveA2AStatus status = ConclaveA2AStatus.Resolve(settings);

        Assert.Equal(ConclaveA2AState.Healthy, status.State);

        Assert.True(status.ConclaveEnabled);

        Assert.False(status.ServerEnabled);

        Assert.False(status.ClientEnabled);

        Assert.Contains("cast_sending", status.Detail, StringComparison.Ordinal);

    }

    [Theory]
    [InlineData(ConclaveA2AState.Disabled, "disabled")]
    [InlineData(ConclaveA2AState.Configured, "configured")]
    [InlineData(ConclaveA2AState.Degraded, "degraded")]
    [InlineData(ConclaveA2AState.Healthy, "healthy")]
    public void WireName_IsTheStableLowercaseState(ConclaveA2AState state, string expected)
    {

        Assert.Equal(expected, ConclaveA2AStatus.WireName(state));

    }

    [Theory]
    [InlineData(ConclaveA2AState.Disabled, HealthStatus.Healthy)]
    [InlineData(ConclaveA2AState.Configured, HealthStatus.Healthy)]
    [InlineData(ConclaveA2AState.Healthy, HealthStatus.Healthy)]
    // Only an enabled-but-broken surface is a health problem. Off and merely-configured are deliberate.
    [InlineData(ConclaveA2AState.Degraded, HealthStatus.Degraded)]
    public void ToHealthStatus_OnlyReportsDegradedForAnEnabledButBrokenSurface(
        ConclaveA2AState state,
        HealthStatus expected)
    {

        Assert.Equal(expected, ConclaveA2AStatus.ToHealthStatus(state));

    }

    [Fact]
    public void BuildHealthComponent_LeadsWithTheStateSoOperatorsCanGrepIt()
    {

        HealthComponentDto component = ConclaveA2AStatus.BuildHealthComponent(new ArcanumSettings());

        Assert.Equal("Conclave", component.Name);

        Assert.Equal(HealthStatus.Healthy, component.Status);

        Assert.StartsWith("state=disabled", component.Detail, StringComparison.Ordinal);

    }

}
