using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Weave;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ProcessEnvironment")]
public sealed class ArcanumHealthCheckerProviderTests : IDisposable
{
    private const string CredentialVariable = "ARCANUM_TEST_HEALTH_REPORT_KEY";
    private readonly string? _originalCredential;

    public ArcanumHealthCheckerProviderTests()
    {
        _originalCredential =
            System.Environment.GetEnvironmentVariable(CredentialVariable);
        System.Environment.SetEnvironmentVariable(CredentialVariable, null);
    }
    [Fact]
    public async Task BuildReportAsync_AllKnownProvidersUnhealthy_ProvidersUnhealthy_OverallDegraded()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "alpha", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost" },
                new ProviderSettings { Name = "beta", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost" },
            ],
        };

        FakeProviderHealthTracker tracker = new();
        tracker.MarkFailed("alpha");
        tracker.MarkFailed("beta");

        ArcanumHealthChecker checker = CreateChecker(settings, tracker);
        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto providers = Assert.Single(report.Components, c => c.Name == "Providers");
        Assert.Equal(HealthStatus.Unhealthy, providers.Status);
        Assert.Equal(HealthStatus.Degraded, report.Status);
    }

    [Fact]
    public async Task BuildReportAsync_PartialProviderFailure_ProvidersDegraded_OverallAtMostDegraded()
    {
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings { Name = "alpha", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost" },
                new ProviderSettings { Name = "beta", Type = AiProviderKind.OpenAICompatible, Endpoint = "http://localhost" },
            ],
        };

        FakeProviderHealthTracker tracker = new();
        tracker.MarkFailed("alpha");
        // beta unobserved = healthy

        ArcanumHealthChecker checker = CreateChecker(settings, tracker);
        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto providers = Assert.Single(report.Components, c => c.Name == "Providers");
        Assert.Equal(HealthStatus.Degraded, providers.Status);
        Assert.True(report.Status is HealthStatus.Healthy or HealthStatus.Degraded);
    }

    [Fact]
    public async Task BuildReportAsync_ReportsCredentialPresenceWithoutSecretMaterial()
    {
        const string secret = "health-report-secret-material";
        System.Environment.SetEnvironmentVariable(CredentialVariable, secret);
        ArcanumSettings settings = new()
        {
            Providers =
            [
                new ProviderSettings
                {
                    Name = "credentialed",
                    Type = AiProviderKind.OpenAICompatible,
                    Endpoint = "https://example.test/v1",
                    CredentialEnvironmentVariable = CredentialVariable,
                },
            ],
        };

        HealthReportDto report = await CreateChecker(
                settings,
                new FakeProviderHealthTracker())
            .BuildReportAsync(CancellationToken.None);
        HealthComponentDto providers =
            Assert.Single(report.Components, component => component.Name == "Providers");

        Assert.Contains(
            "1/1 provider credentials available",
            providers.Detail,
            StringComparison.Ordinal);
        Assert.DoesNotContain(secret, providers.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateOverall_ProvidersUnhealthy_DoesNotMakeOverallUnhealthy()
    {
        HealthStatus overall = ArcanumHealthChecker.AggregateOverall(
        [
            new HealthComponentDto("Grimoire", HealthStatus.Healthy, "ok"),
            new HealthComponentDto("Providers", HealthStatus.Unhealthy, "down"),
            new HealthComponentDto("MCP", HealthStatus.Healthy, "ok"),
        ]);

        Assert.Equal(HealthStatus.Degraded, overall);
    }

    [Fact]
    public async Task BuildReportAsync_LiveProbeFailure_GrimoireUnhealthy_OverallUnhealthy()
    {
        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new FailingLiveness(),
            new EmptyMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new FakeProviderHealthTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto grimoire = Assert.Single(report.Components, c => c.Name == "Grimoire");
        Assert.Equal(HealthStatus.Unhealthy, grimoire.Status);
        Assert.Contains("probe failed", grimoire.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HealthStatus.Unhealthy, report.Status);
    }

    [Fact]
    public async Task BuildReportAsync_NotReady_SkipsLiveProbe()
    {
        CountingLiveness probe = new();
        ArcanumHealthChecker checker = new(
            new NotReadyGrimoire(),
            probe,
            new EmptyMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new FakeProviderHealthTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        Assert.Equal(0, probe.Calls);
        HealthComponentDto grimoire = Assert.Single(report.Components, c => c.Name == "Grimoire");
        Assert.Equal(HealthStatus.Unhealthy, grimoire.Status);
        Assert.Contains("not ready", grimoire.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static ArcanumHealthChecker CreateChecker(ArcanumSettings settings, IProviderHealthTracker tracker) =>
        new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new EmptyMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(settings),
            new WeaveIndexAvailability(),
            tracker);

    private sealed class FakeProviderHealthTracker : IProviderHealthTracker
    {
        private readonly HashSet<string> _unhealthy = new(StringComparer.Ordinal);

        public event Action<ProviderHealthStatus>? HealthChanged;

        public bool IsHealthy(string providerName) => !_unhealthy.Contains(providerName);

        public void MarkFailed(string providerName)
        {
            _ = _unhealthy.Add(providerName);
            HealthChanged?.Invoke(new ProviderHealthStatus(providerName, false, DateTimeOffset.UtcNow, 1));
        }

        public void MarkHealthy(string providerName)
        {
            _ = _unhealthy.Remove(providerName);
            HealthChanged?.Invoke(new ProviderHealthStatus(providerName, true, DateTimeOffset.UtcNow, 0));
        }

        public IReadOnlyList<ProviderHealthStatus> GetAllStatuses() =>
            _unhealthy.Select(n => new ProviderHealthStatus(n, false, DateTimeOffset.UtcNow, 1)).ToArray();
    }

    private sealed class ReadyGrimoire : IGrimoireDbReadiness
    {
        public bool IsReady => true;

        public void MarkReady()
        {
        }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void MarkFailed(Exception exception)
        {
        }
    }

    private sealed class NotReadyGrimoire : IGrimoireDbReadiness
    {
        public bool IsReady => false;

        public void MarkReady()
        {
        }

        public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void MarkFailed(Exception exception)
        {
        }
    }

    private sealed class AlwaysOkLiveness : IGrimoireLivenessProbe
    {
        public Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "Database ready."));
    }

    private sealed class FailingLiveness : IGrimoireLivenessProbe
    {
        public Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((false, "Grimoire live probe failed: SqliteException."));
    }

    private sealed class CountingLiveness : IGrimoireLivenessProbe
    {
        public int Calls { get; private set; }

        public Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult((true, "Database ready."));
        }
    }

    private sealed class EmptyMcpManager : IMcpConnectionManager
    {
        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Result> StartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result> StopAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Result> RestartAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<McpServerInfo?> GetStatusAsync(string name, string? workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<Microsoft.Extensions.AI.AITool>> GetAvailableToolsAsync(string? workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<Microsoft.Extensions.AI.AIFunction?> GetToolAsync(
            string serverName,
            string toolName,
            string? workingDirectory,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(
            CredentialVariable,
            _originalCredential);
    }
}
