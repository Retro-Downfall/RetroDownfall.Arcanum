using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Support;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class ArcanumHealthCheckerMcpTests
{

    [Fact]
    public async Task BuildReportAsync_all_mcp_servers_down_is_unhealthy()
    {

        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new AllDownMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new AlwaysHealthyTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto mcp = Assert.Single(report.Components, c => c.Name == "MCP");

        Assert.Equal(HealthStatus.Unhealthy, mcp.Status);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);

    }

    [Fact]
    public async Task BuildReportAsync_on_demand_mcp_server_stopped_is_healthy()
    {

        // alwaysOn: false servers are deliberately never started at bootstrap (DESIGN §1800), so a
        // correctly configured on-demand server sitting in Stopped must not drag health down.
        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new StubMcpManager(
            [
                Server("lazy", alwaysOn: false, McpServerState.Stopped),
            ]),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new AlwaysHealthyTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto mcp = Assert.Single(report.Components, static c => c.Name == "MCP");

        Assert.Equal(HealthStatus.Healthy, mcp.Status);

        // Overall must not be Unhealthy: HealthEndpoints maps Unhealthy to 503, so an on-demand
        // server would otherwise make readiness probes treat the host as dead forever.
        Assert.NotEqual(HealthStatus.Unhealthy, report.Status);

        Assert.Contains("on-demand", mcp.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task BuildReportAsync_mixed_mcp_scope_counts_always_on_only()
    {

        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new StubMcpManager(
            [
                Server("eager", alwaysOn: true, McpServerState.Running),
                Server("lazy", alwaysOn: false, McpServerState.Stopped),
            ]),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new AlwaysHealthyTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto mcp = Assert.Single(report.Components, static c => c.Name == "MCP");

        Assert.Equal(HealthStatus.Healthy, mcp.Status);

        Assert.Contains("1/1 always-on running", mcp.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task BuildReportAsync_failed_always_on_mcp_server_is_unhealthy_despite_on_demand_peers()
    {

        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new StubMcpManager(
            [
                Server("eager", alwaysOn: true, McpServerState.Error, "boom"),
                Server("lazy", alwaysOn: false, McpServerState.Stopped),
            ]),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new AlwaysHealthyTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto mcp = Assert.Single(report.Components, static c => c.Name == "MCP");

        Assert.Equal(HealthStatus.Unhealthy, mcp.Status);

        Assert.Contains("eager: boom", mcp.Detail, StringComparison.Ordinal);

    }

    [Fact]
    public async Task BuildReportAsync_includes_tool_child_sandbox_component()
    {

        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new EmptyMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new AlwaysHealthyTracker());

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto sandbox = Assert.Single(report.Components, c => c.Name == "ToolChildSandbox");

        Assert.NotNull(sandbox.Detail);

        Assert.Contains("network=", sandbox.Detail, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("NotProvided", sandbox.Detail, StringComparison.Ordinal);

        // On macOS with sandbox-exec: Healthy; elsewhere typically Degraded for beta honesty.
        Assert.True(
            sandbox.Status is HealthStatus.Healthy or HealthStatus.Degraded,
            $"unexpected status {sandbox.Status}");

    }

    [Fact]
    public async Task BuildReportAsync_includes_workspace_check_unavailable_reason()
    {

        StubWorkspaceCheckCapability capability = new(
            new WorkspaceCheckCapabilityStatus(
                false,
                true,
                "Linux mandatory jail unavailable."));
        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AlwaysOkLiveness(),
            new EmptyMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()),
            new WeaveIndexAvailability(),
            new AlwaysHealthyTracker(),
            capability);

        HealthReportDto report = await checker.BuildReportAsync(
            CancellationToken.None);
        HealthComponentDto workspaceCheck = Assert.Single(
            report.Components,
            static component => component.Name == "WorkspaceCheck");

        Assert.Equal(HealthStatus.Degraded, workspaceCheck.Status);
        Assert.Contains(
            "Linux mandatory jail unavailable",
            workspaceCheck.Detail,
            StringComparison.Ordinal);
    }

    private static McpServerInfo Server(
        string name,
        bool alwaysOn,
        McpServerState state,
        string? errorMessage = null) =>
        new(
            name,
            null,
            McpServerTransport.Stdio,
            alwaysOn,
            "noop",
            [],
            null,
            state,
            errorMessage,
            [],
            null);

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

    private sealed class AlwaysOkLiveness : IGrimoireLivenessProbe
    {
        public Task<(bool Ok, string Detail)> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult((true, "Database ready."));
    }

    private sealed class EmptyMcpManager : IMcpConnectionManager
    {

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Array.Empty<McpServerInfo>());

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

    private sealed class AllDownMcpManager : IMcpConnectionManager
    {

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<McpServerInfo[]>(
            [
                new McpServerInfo(
                    "alpha",
                    null,
                    McpServerTransport.Stdio,
                    AlwaysOn: true,
                    "noop",
                    [],
                    null,
                    McpServerState.Error,
                    "down",
                    [],
                    null),
            ]);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

    private sealed class StubMcpManager(McpServerInfo[] servers) : IMcpConnectionManager
    {

        public Task<McpServerInfo[]> GetAllStatusesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(servers);

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAllAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

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

    private sealed class AlwaysHealthyTracker : RetroDownfall.Arcanum.Core.Resilience.IProviderHealthTracker
    {
        public event Action<RetroDownfall.Arcanum.Core.Resilience.ProviderHealthStatus>? HealthChanged
        {
            add { }
            remove { }
        }

        public bool IsHealthy(string providerName) => true;

        public void MarkFailed(string providerName)
        {
        }

        public void MarkHealthy(string providerName)
        {
        }

        public IReadOnlyList<RetroDownfall.Arcanum.Core.Resilience.ProviderHealthStatus> GetAllStatuses() => [];
    }

    private sealed class StubWorkspaceCheckCapability(
        WorkspaceCheckCapabilityStatus status)
        : IWorkspaceCheckCapabilityReporter
    {

        public bool IsCurrentlyEligible => status.IsAvailable;

        public WorkspaceCheckCapabilityStatus GetStatus(
            string? workspaceRoot = null) =>
            status;
    }

}
