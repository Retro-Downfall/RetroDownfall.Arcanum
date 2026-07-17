using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Health;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed class ArcanumHealthCheckerMcpTests
{

    [Fact]
    public async Task BuildReportAsync_all_mcp_servers_down_is_unhealthy()
    {

        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new AllDownMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

        HealthReportDto report = await checker.BuildReportAsync(CancellationToken.None);

        HealthComponentDto mcp = Assert.Single(report.Components, c => c.Name == "MCP");

        Assert.Equal(HealthStatus.Unhealthy, mcp.Status);

        Assert.Equal(HealthStatus.Unhealthy, report.Status);

    }

    [Fact]
    public async Task BuildReportAsync_includes_tool_child_sandbox_component()
    {

        ArcanumHealthChecker checker = new(
            new ReadyGrimoire(),
            new EmptyMcpManager(),
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings()));

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

    private sealed class ReadyGrimoire : IGrimoireDbReadiness
    {

        public bool IsReady => true;

        public void MarkReady()
        {
        }

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

        public Task<List<McpServerStatusDto>> GetServerStatusesAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task ReloadAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Result> TrustWorkspaceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

    }

}
