using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Api.Health;

public sealed class ArcanumHealthChecker(
    IGrimoireDbReadiness grimoireReadiness,
    IMcpConnectionManager mcpConnectionManager,
    IOptionsMonitor<ArcanumSettings> settings)
{

    public async Task<HealthReportDto> BuildReportAsync(CancellationToken cancellationToken)
    {

        List<HealthComponentDto> components = [];

        HealthStatus grimoireStatus = grimoireReadiness.IsReady ? HealthStatus.Healthy : HealthStatus.Unhealthy;

        components.Add(new HealthComponentDto(
            "Grimoire",
            grimoireStatus,
            grimoireReadiness.IsReady ? "Database ready." : "Grimoire database is not ready."));

        McpServerInfo[] mcpServers = await mcpConnectionManager
            .GetAllStatusesAsync(cancellationToken)
            .ConfigureAwait(false);

        int mcpTotal = mcpServers.Length;

        int mcpHealthy = mcpServers.Count(static s => s.State == McpServerState.Running);

        string[] mcpFailures = mcpServers
            .Where(static s => s.State is McpServerState.Error or McpServerState.Stopped && s.AlwaysOn)
            .Select(static s => string.IsNullOrWhiteSpace(s.ErrorMessage) ? s.Name : $"{s.Name}: {s.ErrorMessage}")
            .ToArray();

        HealthStatus mcpStatus = mcpTotal == 0
            ? HealthStatus.Healthy
            : mcpHealthy == mcpTotal
                ? HealthStatus.Healthy
                : mcpHealthy > 0
                    ? HealthStatus.Degraded
                    : HealthStatus.Unhealthy;

        string mcpDetail = mcpFailures.Length > 0
            ? $"{mcpHealthy}/{mcpTotal} running. Failed always-on: {string.Join("; ", mcpFailures)}"
            : $"{mcpHealthy}/{mcpTotal} running.";

        components.Add(new HealthComponentDto("MCP", mcpStatus, mcpDetail));

        int providerCount = (settings.CurrentValue.Providers ?? []).Length;

        components.Add(new HealthComponentDto(
            "Providers",
            providerCount > 0 ? HealthStatus.Healthy : HealthStatus.Degraded,
            providerCount > 0
                ? $"{providerCount} providers configured; reachability is tracked by resilience probes."
                : "No providers configured."));

        bool escapeHatch = settings.CurrentValue.Security?.AllowUnsandboxedToolChildren ?? false;

        ToolChildSandboxStatus sandbox = ToolChildSandboxCapabilityReporter.BuildForCurrentHost(escapeHatch);

        components.Add(new HealthComponentDto(
            "ToolChildSandbox",
            sandbox.IsHealthDegraded ? HealthStatus.Degraded : HealthStatus.Healthy,
            $"{sandbox.Platform}: FS jail={sandbox.FilesystemJailMode}; "
            + $"rlimits={sandbox.ResourceLimitsMode}; "
            + $"network={sandbox.NetworkIsolationMode}; "
            + $"escapeHatch={sandbox.EscapeHatchEnabled}. "
            + sandbox.PublicMessage));

        HealthStatus overall = components.Any(static c => c.Status == HealthStatus.Unhealthy)
            ? HealthStatus.Unhealthy
            : components.Any(static c => c.Status == HealthStatus.Degraded)
                ? HealthStatus.Degraded
                : HealthStatus.Healthy;

        return new HealthReportDto(overall, components.ToArray());

    }

}
