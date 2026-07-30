using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Resilience;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;
using RetroDownfall.Arcanum.Infrastructure.Operations;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Api.Health;

public sealed class ArcanumHealthChecker(
    IGrimoireDbReadiness grimoireReadiness,
    IGrimoireLivenessProbe grimoireLiveness,
    IMcpConnectionManager mcpConnectionManager,
    IOptionsMonitor<ArcanumSettings> settings,
    WeaveIndexAvailability weaveIndexAvailability,
    IProviderHealthTracker providerHealthTracker,
    IWorkspaceCheckCapabilityReporter? workspaceCheckCapabilityReporter = null,
    LongRunningOperationReconciliationStatus? operationReconciliationStatus = null,
    IEncryptedBlobDiagnostics? encryptedBlobDiagnostics = null)
{

    public async Task<HealthReportDto> BuildReportAsync(CancellationToken cancellationToken)
    {

        List<HealthComponentDto> components = [];

        HealthStatus grimoireStatus;
        string grimoireDetail;

        if (!grimoireReadiness.IsReady)
        {
            grimoireStatus = HealthStatus.Unhealthy;
            grimoireDetail = "Grimoire database is not ready.";
        }
        else
        {
            (bool liveOk, string liveDetail) = await grimoireLiveness
                .ProbeAsync(cancellationToken)
                .ConfigureAwait(false);

            if (liveOk)
            {
                grimoireStatus = HealthStatus.Healthy;
                grimoireDetail = liveDetail;
            }
            else
            {
                // Readiness-critical: overall Unhealthy → 503 is appropriate when the DB is dead
                // after MarkReady (startup latch is intentionally left set).
                grimoireStatus = HealthStatus.Unhealthy;
                grimoireDetail = liveDetail;
            }
        }

        components.Add(new HealthComponentDto(
            "Grimoire",
            grimoireStatus,
            grimoireDetail));

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

        components.Add(BuildProvidersComponent(settings.CurrentValue, providerHealthTracker));

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

        HostProcessToolPolicyStatus hostTools = HostProcessToolPolicy.Resolve(
            ArcanumEnvironment.ResolveEdition(settings.CurrentValue.Edition));

        components.Add(new HealthComponentDto(
            "HostProcessTools",
            hostTools.IsHealthDegraded ? HealthStatus.Degraded : HealthStatus.Healthy,
            $"edition={hostTools.Edition}; allowed={hostTools.Allowed}; "
            + $"escapeHatchEnv={hostTools.EscapeHatchEnvSet}. {hostTools.PublicMessage}"));

        WorkspaceCheckCapabilityStatus workspaceCheck =
            workspaceCheckCapabilityReporter is null
            ? new WorkspaceCheckCapabilityStatus(
                false,
                false,
                "Workspace-check capability reporter is unavailable.")
            : await workspaceCheckCapabilityReporter
                .GetStatusAsync(
                    settings.CurrentValue.ResolveDefaultWorkspace(),
                    cancellationToken)
                .ConfigureAwait(false);

        components.Add(new HealthComponentDto(
            "WorkspaceCheck",
            workspaceCheck.IsHealthDegraded
                ? HealthStatus.Degraded
                : HealthStatus.Healthy,
            $"available={workspaceCheck.IsAvailable}. {workspaceCheck.Reason}"));

        (bool embeddingsEnabled, string vectorMode, string vectorDiagnostic, int managedBudget) =
            EmbeddingsVectorStatus.Resolve(
                settings.CurrentValue.ResolveEmbeddings(),
                weaveIndexAvailability);

        HealthStatus embeddingsHealth = vectorMode switch
        {
            WeaveIndexAvailability.ModeUnavailable => HealthStatus.Degraded,
            _ => HealthStatus.Healthy,
        };

        string embeddingsDetail = embeddingsEnabled
            ? $"mode={vectorMode}; budget={managedBudget}. {vectorDiagnostic}"
            : $"disabled. {vectorDiagnostic}";

        components.Add(new HealthComponentDto("Embeddings", embeddingsHealth, embeddingsDetail));

        LongRunningOperationReconciliationSnapshot operationSnapshot =
            operationReconciliationStatus?.Snapshot
            ?? new LongRunningOperationReconciliationSnapshot(
                StartupCompleted: false,
                Deferred: true,
                LastRunAt: null,
                LastSummary: null,
                PublicDetail: "Durable operation reconciliation status is unavailable.");
        bool operationAttention = operationSnapshot.Deferred
            || !operationSnapshot.StartupCompleted
            || operationSnapshot.LastSummary?.RequiresAttention > 0;
        components.Add(new HealthComponentDto(
            "DurableOperations",
            operationAttention ? HealthStatus.Degraded : HealthStatus.Healthy,
            operationSnapshot.PublicDetail ?? "Durable operation reconciliation completed."));

        components.Add(await BuildFileEncryptionComponentAsync(
                encryptedBlobDiagnostics,
                cancellationToken)
            .ConfigureAwait(false));

        HealthStatus overall = AggregateOverall(components);

        return new HealthReportDto(overall, components.ToArray());

    }

    private static async Task<HealthComponentDto> BuildFileEncryptionComponentAsync(
        IEncryptedBlobDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        if (diagnostics is null)
        {
            return new HealthComponentDto(
                "FileEncryption",
                HealthStatus.Degraded,
                "File-encryption diagnostics are unavailable.");
        }

        try
        {
            FileEncryptionDiagnostics result = await diagnostics
                .InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            return new HealthComponentDto(
                "FileEncryption",
                result.IsHealthy ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                result.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HealthComponentDto(
                "FileEncryption",
                HealthStatus.Unhealthy,
                $"File-encryption diagnostics failed ({ex.GetType().Name}).");
        }
    }

    internal static HealthComponentDto BuildProvidersComponent(
        ArcanumSettings arcanumSettings,
        IProviderHealthTracker tracker)
    {
        ProviderSettings[] providers = arcanumSettings.Providers ?? [];
        int providerCount = providers.Length;

        if (providerCount == 0)
        {
            return new HealthComponentDto(
                "Providers",
                HealthStatus.Degraded,
                "No providers configured.");
        }

        int healthy = 0;
        int unhealthy = 0;
        int credentialBacked = 0;
        foreach (ProviderSettings provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                continue;
            }

            if (EnvironmentCredentialResolver.ResolveProviderApiKey(provider) is not null)
            {
                credentialBacked++;
            }

            // Unobserved providers are assumed healthy per IProviderHealthTracker contract.
            if (tracker.IsHealthy(provider.Name))
            {
                healthy++;
            }
            else
            {
                unhealthy++;
            }
        }

        int observed = healthy + unhealthy;
        if (observed == 0)
        {
            return new HealthComponentDto(
                "Providers",
                HealthStatus.Degraded,
                "No named providers configured.");
        }

        HealthStatus status = unhealthy == 0
            ? HealthStatus.Healthy
            : healthy == 0
                ? HealthStatus.Unhealthy
                : HealthStatus.Degraded;

        return new HealthComponentDto(
            "Providers",
            status,
            $"{healthy}/{observed} providers healthy (resilience probes); "
            + $"{credentialBacked}/{observed} provider credentials available from environment.");
    }

    /// <summary>
    /// Providers Unhealthy/Degraded contribute at most Degraded to overall readiness.
    /// Overall Unhealthy only from readiness-critical components (e.g. Grimoire, MCP).
    /// </summary>
    internal static HealthStatus AggregateOverall(IReadOnlyList<HealthComponentDto> components)
    {
        HealthStatus worst = HealthStatus.Healthy;
        foreach (HealthComponentDto component in components)
        {
            HealthStatus effective = component.Status;
            if (string.Equals(component.Name, "Providers", StringComparison.Ordinal)
                && effective == HealthStatus.Unhealthy)
            {
                effective = HealthStatus.Degraded;
            }

            if (effective == HealthStatus.Unhealthy)
            {
                return HealthStatus.Unhealthy;
            }

            if (effective == HealthStatus.Degraded)
            {
                worst = HealthStatus.Degraded;
            }
        }

        return worst;
    }

}
