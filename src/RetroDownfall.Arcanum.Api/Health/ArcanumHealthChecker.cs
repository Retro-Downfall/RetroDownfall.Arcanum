using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Environment;
using RetroDownfall.Arcanum.Core.Mcp;
using RetroDownfall.Arcanum.Core.Operations;
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
    IEncryptedBlobDiagnostics? encryptedBlobDiagnostics = null,
    IProviderApiKeyResolver? providerApiKeyResolver = null,
    IDurableOperationDiagnostics? operationDiagnosticsSource = null)
{

    private readonly IProviderApiKeyResolver _providerApiKeyResolver =
        providerApiKeyResolver ?? EnvironmentOnlyProviderApiKeyResolver.Instance;

    /// <summary>
    /// A health probe must never fail because diagnostics failed. An unreadable ledger degrades to
    /// "unknown" rather than taking the whole report down.
    /// </summary>
    private static async Task<DurableOperationDiagnosticsReport> SafeInspectAsync(
        IDurableOperationDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await diagnostics
                .InspectAsync(DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return DurableOperationDiagnosticsReport.Empty;
        }
    }

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

        // Only always-on servers are readiness-scoped: `alwaysOn: false` entries are deliberately
        // left Stopped until something starts them, so counting them would report a correctly
        // configured on-demand server as a bootstrap failure forever.
        McpServerInfo[] mcpAlwaysOn = mcpServers.Where(static s => s.AlwaysOn).ToArray();

        int mcpTotal = mcpAlwaysOn.Length;

        int mcpHealthy = mcpAlwaysOn.Count(static s => s.State == McpServerState.Running);

        int onDemandTotal = mcpServers.Length - mcpTotal;

        int onDemandStopped = mcpServers.Count(static s => !s.AlwaysOn && s.State != McpServerState.Running);

        string[] mcpFailures = mcpAlwaysOn
            .Where(static s => s.State is McpServerState.Error or McpServerState.Stopped)
            .Select(static s => string.IsNullOrWhiteSpace(s.ErrorMessage) ? s.Name : $"{s.Name}: {s.ErrorMessage}")
            .ToArray();

        HealthStatus mcpStatus = mcpTotal == 0
            ? HealthStatus.Healthy
            : mcpHealthy == mcpTotal
                ? HealthStatus.Healthy
                : mcpHealthy > 0
                    ? HealthStatus.Degraded
                    : HealthStatus.Unhealthy;

        string mcpDetail = $"{mcpHealthy}/{mcpTotal} always-on running."
            + (onDemandTotal > 0 ? $" {onDemandStopped}/{onDemandTotal} on-demand not running." : string.Empty)
            + (mcpFailures.Length > 0 ? $" Failed always-on: {string.Join("; ", mcpFailures)}" : string.Empty);

        components.Add(new HealthComponentDto("MCP", mcpStatus, mcpDetail));

        components.Add(
            await BuildProvidersComponentAsync(
                    settings.CurrentValue,
                    providerHealthTracker,
                    _providerApiKeyResolver,
                    cancellationToken)
                .ConfigureAwait(false));

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

        components.Add(ConclaveA2AStatus.BuildHealthComponent(settings.CurrentValue));

        LongRunningOperationReconciliationSnapshot operationSnapshot =
            operationReconciliationStatus?.Snapshot
            ?? new LongRunningOperationReconciliationSnapshot(
                StartupCompleted: false,
                Deferred: true,
                LastRunAt: null,
                LastSummary: null,
                PublicDetail: "Durable operation reconciliation status is unavailable.");
        // The reconciliation snapshot only says what the last pass did. Issue #40 also requires the
        // states that pass could not fix — stale leases, operations recovery gave up on, and kinds
        // with no owning handler — to be visible with the guidance for repairing them.
        DurableOperationDiagnosticsReport operationDiagnostics = operationDiagnosticsSource is null
            ? DurableOperationDiagnosticsReport.Empty
            : await SafeInspectAsync(operationDiagnosticsSource, cancellationToken).ConfigureAwait(false);

        bool operationAttention = operationSnapshot.Deferred
            || !operationSnapshot.StartupCompleted
            || operationSnapshot.LastSummary?.RequiresAttention > 0
            || operationDiagnostics.NeedsAttention;
        components.Add(new HealthComponentDto(
            "DurableOperations",
            operationAttention ? HealthStatus.Degraded : HealthStatus.Healthy,
            $"{operationSnapshot.PublicDetail ?? "Durable operation reconciliation completed."} "
            + operationDiagnostics.Describe()));

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

    /// <summary>
    /// Reports provider readiness and credential <em>presence</em> only. The resolved credential is
    /// never retained, logged, or echoed into the component detail (§8.1).
    /// </summary>
    internal static async Task<HealthComponentDto> BuildProvidersComponentAsync(
        ArcanumSettings arcanumSettings,
        IProviderHealthTracker tracker,
        IProviderApiKeyResolver apiKeyResolver,
        CancellationToken cancellationToken)
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

            if (await apiKeyResolver
                    .ResolveAsync(provider, cancellationToken)
                    .ConfigureAwait(false) is not null)
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
            + $"{credentialBacked}/{observed} provider credentials available from the environment "
            + "reference or the secure store.");
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
