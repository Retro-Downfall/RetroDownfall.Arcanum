using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Platform;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Repositories;

namespace RetroDownfall.Arcanum.Infrastructure.Security;

/// <summary>
/// Enforces per-campaign Sanctum boundaries for path, network, and tool restrictions.
/// </summary>
/// <remarks>
/// On macOS, network enforcement is advisory only (no kernel-level firewall): breaches are always
/// logged; in <see cref="SanctumMode.Strict"/> mode the application blocks the tool call.
/// <see cref="ResourceLimits.MaxFileWriteMb"/> is enforced on in-process file-write tools;
/// runtime process/memory enforcement is deferred to phase 2 (container backend).
/// Breaches are recorded inline to <see cref="ISanctumBreachRepository"/> (Grimoire-backed):
/// both this guard and the repository are scoped and share the same <c>ArcanumDbContext</c>, so no
/// fire-and-forget is needed. Breaches raised for an unparseable/unknown campaign id are logged only
/// (not persisted), since the table's foreign key requires an existing campaign row.
/// </remarks>
public sealed class SanctumGuard(
    ICampaignRepository campaignRepository,
    ISanctumBreachRepository breachRepository,
    ILogger<SanctumGuard> logger) : ISanctumGuard
{

    public async Task<SanctumResult> ValidatePathAsync(
        string campaignId,
        string requestedPath,
        string operationType,
        string toolName,
        CancellationToken ct = default)
    {
        SanctumResult? invalidCampaign = DenyIfInvalidCampaignId(campaignId, toolName, "PathEscape");

        if (invalidCampaign is not null)
        {
            return invalidCampaign;
        }

        (Campaign? campaign, SanctumConfig? config) = await TryLoadCampaignAndConfigAsync(campaignId, ct).ConfigureAwait(false);

        if (campaign is null || config is null || !config.Enabled || !config.EnforcePathBoundary)
        {
            return AllowedResult();
        }

        string workspaceRoot;

        try
        {
            workspaceRoot = Path.GetFullPath(campaign.Path.Trim());
        }
        catch (Exception)
        {
            return await DenyPathAsync(
                campaignId,
                toolName,
                requestedPath,
                null,
                campaign.Path,
                "The campaign workspace path could not be resolved.",
                config.MaxBreachCount,
                ct).ConfigureAwait(false);
        }

        string candidateFull;

        try
        {
            candidateFull = Path.GetFullPath(requestedPath.Trim());
        }
        catch (Exception)
        {
            return await DenyPathAsync(
                campaignId,
                toolName,
                requestedPath,
                null,
                workspaceRoot,
                $"Invalid path for {operationType}.",
                config.MaxBreachCount,
                ct).ConfigureAwait(false);
        }

        if (IsUnderAllowedRoots(workspaceRoot, candidateFull, config.AllowedPaths))
        {
            return AllowedResult();
        }

        return await DenyPathAsync(
            campaignId,
            toolName,
            requestedPath,
            candidateFull,
            workspaceRoot,
            $"Path for {operationType} would leave the campaign workspace.",
            config.MaxBreachCount,
            ct).ConfigureAwait(false);
    }

    public async Task<SanctumResult> ValidateNetworkAsync(
        string campaignId,
        string url,
        string toolName,
        CancellationToken ct = default)
    {
        SanctumResult? invalidCampaign = DenyIfInvalidCampaignId(campaignId, toolName, "NetworkEgress");

        if (invalidCampaign is not null)
        {
            return invalidCampaign;
        }

        (_, SanctumConfig? config) = await TryLoadCampaignAndConfigAsync(campaignId, ct).ConfigureAwait(false);

        if (config is null || !config.Enabled)
        {
            return AllowedResult();
        }

        if (config.NetworkPolicy == NetworkPolicy.AllowAll)
        {
            return AllowedResult();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return AllowedResult();
        }

        if (config.NetworkPolicy == NetworkPolicy.DenyAll)
        {
            return await DenyNetworkAsync(
                campaignId,
                toolName,
                url,
                "Network egress is denied by this Sanctum.",
                config.MaxBreachCount,
                ct).ConfigureAwait(false);
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return await DenyNetworkAsync(
                campaignId,
                toolName,
                url,
                "The outbound URL is not a valid HTTP or HTTPS address.",
                config.MaxBreachCount,
                ct).ConfigureAwait(false);
        }

        string host = uri.Host;

        if (await IsHostAllowedAsync(host, config.AllowedDomains, ct).ConfigureAwait(false))
        {
            return AllowedResult();
        }

        return await DenyNetworkAsync(
            campaignId,
            toolName,
            url,
            $"Host '{host}' is not in the Sanctum allowed domain list.",
            config.MaxBreachCount,
            ct).ConfigureAwait(false);
    }

    public async Task<SanctumResult> ValidateToolAsync(string campaignId, string toolName, CancellationToken ct = default)
    {
        SanctumResult? invalidCampaign = DenyIfInvalidCampaignId(campaignId, toolName, "DisabledTool");

        if (invalidCampaign is not null)
        {
            return invalidCampaign;
        }

        (_, SanctumConfig? config) = await TryLoadCampaignAndConfigAsync(campaignId, ct).ConfigureAwait(false);

        if (config is null || !config.Enabled)
        {
            return AllowedResult();
        }

        if (config.DisabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            SanctumBreach breach = CreateBreach(
                campaignId,
                toolName,
                "DisabledTool",
                $"Tool '{toolName}' is disabled in this Sanctum.");

            await RecordBreachAsync(breach, config.MaxBreachCount, ct).ConfigureAwait(false);

            return new SanctumResult
            {
                Allowed = false,
                DenyReason = breach.Detail,
                Breach = breach,
            };
        }

        return AllowedResult();
    }

    public async Task<ResourceLimits> GetEffectiveResourceLimitsForWorkspaceAsync(
        string? workspaceRoot,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return ClampResourceLimits(new ResourceLimits());
        }

        Campaign? campaign = await campaignRepository
            .GetByPathAsync(workspaceRoot.Trim(), ct)
            .ConfigureAwait(false);

        if (campaign is null)
        {
            return ClampResourceLimits(new ResourceLimits());
        }

        SanctumConfig config = CampaignRepository.GetSanctumConfig(campaign);

        return ClampResourceLimits(config.ResourceLimits);
    }

    public async Task<SanctumChildProcessBoundary?> GetChildProcessBoundaryForWorkspaceAsync(
        string? workspaceRoot,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return null;
        }

        string normalizedRoot;

        try
        {
            normalizedRoot = Path.GetFullPath(workspaceRoot.Trim());
        }
        catch (Exception)
        {
            return null;
        }

        Campaign? campaign = await campaignRepository
            .GetByPathAsync(normalizedRoot, ct)
            .ConfigureAwait(false);

        if (campaign is null)
        {
            return null;
        }

        SanctumConfig config = CampaignRepository.GetSanctumConfig(campaign);

        string campaignWorkspace;

        try
        {
            campaignWorkspace = Path.GetFullPath(campaign.Path.Trim());
        }
        catch (Exception)
        {
            campaignWorkspace = normalizedRoot;
        }

        return new SanctumChildProcessBoundary(
            campaignWorkspace,
            PathBoundaryRequired: config.Enabled && config.EnforcePathBoundary,
            AllowedPaths: config.AllowedPaths);
    }

    public async Task RecordResourceLimitBreachAsync(
        string? workspaceRoot,
        string toolName,
        ResourceLimitKind resource,
        string limitValue,
        string? actualValue,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return;
        }

        Campaign? campaign = await campaignRepository
            .GetByPathAsync(workspaceRoot.Trim(), ct)
            .ConfigureAwait(false);

        if (campaign is null)
        {
            // No known campaign for this path: breach persistence requires an existing campaign row
            // (foreign key). Enforcement/denial has already happened by the time this is called;
            // this is audit-trail only, so a log-only fallback is acceptable here.
            logger.LogWarning(
                "Sanctum ResourceLimit breach for tool {ToolName} could not be persisted: workspace path did not resolve to a known campaign.",
                toolName);

            return;
        }

        SanctumConfig config = CampaignRepository.GetSanctumConfig(campaign);

        string resourceName = DescribeResource(resource);

        SanctumBreach breach = CreateBreach(
            campaign.Id.ToString(),
            toolName,
            "ResourceLimit",
            $"Tool '{toolName}' exceeded {resourceName} limit: {actualValue ?? "unknown"} > {limitValue}");

        SanctumBreachRecord record = new(
            Id: string.Empty,
            CampaignId: breach.CampaignId,
            OccurredAt: breach.Timestamp,
            ToolName: breach.ToolName,
            BreachType: breach.BreachType,
            Description: breach.Detail,
            Details: new SanctumBreachDetails(
                RequestedPath: null,
                ResolvedPath: null,
                WorkspaceRoot: workspaceRoot,
                RequestedUrl: null,
                ToolArguments: null,
                LimitValue: limitValue,
                ActualValue: actualValue));

        LogBreach(breach);

        try
        {
            await breachRepository.RecordAsync(record, config.MaxBreachCount, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Persistence failure must not surface to the model: the denial has already been decided
            // by the caller. Only the audit trail write is best-effort here.
            logger.LogError(
                ex,
                "Failed to persist Sanctum ResourceLimit breach for campaign {CampaignId}.",
                breach.CampaignId);
        }
    }

    private static string DescribeResource(ResourceLimitKind resource) => resource switch
    {
        ResourceLimitKind.Cpu => "CPU time",
        ResourceLimitKind.Memory => "memory",
        ResourceLimitKind.FileDescriptors => "open file descriptor",
        _ => "resource",
    };

    private static ResourceLimits ClampResourceLimits(ResourceLimits limits) =>
        limits with
        {
            MaxProcessMemoryMb = ArcanumSettingClamps.SanctumMaxProcessMemoryMb(limits.MaxProcessMemoryMb),
            MaxProcessCount = ArcanumSettingClamps.SanctumMaxProcessCount(limits.MaxProcessCount),
            MaxFileWriteMb = ArcanumSettingClamps.SanctumMaxFileWriteMb(limits.MaxFileWriteMb),
            ProcessTimeoutSeconds = ArcanumSettingClamps.SanctumProcessTimeoutSeconds(limits.ProcessTimeoutSeconds),
            MaxCpuSeconds = ArcanumSettingClamps.SanctumMaxCpuSeconds(limits.MaxCpuSeconds),
            MaxMemoryMb = ArcanumSettingClamps.SanctumMaxMemoryMb(limits.MaxMemoryMb),
            MaxFileDescriptors = ArcanumSettingClamps.SanctumMaxFileDescriptors(limits.MaxFileDescriptors),
        };

    private async Task<(Campaign? Campaign, SanctumConfig? Config)> TryLoadCampaignAndConfigAsync(
        string campaignId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(campaignId, out Guid id))
        {
            return (null, null);
        }

        Campaign? campaign = await campaignRepository.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (campaign is null)
        {
            return (null, null);
        }

        SanctumConfig config = CampaignRepository.GetSanctumConfig(campaign);

        return (campaign, config);
    }

    private static bool IsUnderAllowedRoots(string workspaceRoot, string candidateFull, IReadOnlyList<string> allowedPaths)
    {
        if (WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(workspaceRoot, candidateFull, out _))
        {
            return true;
        }

        foreach (string allowed in allowedPaths)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            string allowedFull;

            try
            {
                allowedFull = Path.GetFullPath(allowed.Trim());
            }
            catch (Exception)
            {
                continue;
            }

            if (WorkspacePathPolicy.IsPathUnderWorkspaceWithSymlinkCheck(allowedFull, candidateFull, out _))
            {
                return true;
            }
        }

        return false;
    }

    private SanctumResult? DenyIfInvalidCampaignId(string campaignId, string toolName, string breachType)
    {
        if (string.IsNullOrWhiteSpace(campaignId))
        {
            return null;
        }

        if (Guid.TryParse(campaignId, out _))
        {
            return null;
        }

        SanctumBreach breach = CreateBreach(
            campaignId,
            toolName,
            breachType,
            "Invalid campaign identifier.");

        // Log-only: the campaign id does not resolve to an existing campaign row, so persisting
        // would violate the SanctumBreaches -> Campaigns foreign key. Enforcement still denies.
        LogBreach(breach);

        return new SanctumResult
        {
            Allowed = false,
            DenyReason = breach.Detail,
            Breach = RedactBreachForApi(breach),
        };
    }

    private static async Task<bool> IsHostAllowedAsync(
        string host,
        IReadOnlyList<string> allowedDomains,
        CancellationToken ct)
    {
        if (allowedDomains.Count == 0)
        {
            return false;
        }

        if (IsHostAllowed(host, allowedDomains))
        {
            return true;
        }

        IPAddress[] requestAddresses;

        try
        {
            requestAddresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return false;
        }

        foreach (string domain in allowedDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            string normalized = domain.Trim().TrimStart('.');

            if (IPAddress.TryParse(normalized, out IPAddress? allowedIp))
            {
                foreach (IPAddress requestAddress in requestAddresses)
                {
                    if (requestAddress.Equals(allowedIp))
                    {
                        return true;
                    }
                }

                continue;
            }

            IPAddress[] allowedAddresses;

            try
            {
                allowedAddresses = await Dns.GetHostAddressesAsync(normalized, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }

            foreach (IPAddress requestAddress in requestAddresses)
            {
                foreach (IPAddress allowedAddress in allowedAddresses)
                {
                    if (requestAddress.Equals(allowedAddress))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static SanctumBreach RedactBreachForApi(SanctumBreach breach) =>
        breach with
        {
            RequestedPath = SanctumPathRedactor.Redact(breach.RequestedPath),
            ResolvedPath = SanctumPathRedactor.Redact(breach.ResolvedPath),
            WorkspaceRoot = SanctumPathRedactor.Redact(breach.WorkspaceRoot),
        };

    private static bool IsHostAllowed(string host, IReadOnlyList<string> allowedDomains)
    {

        foreach (string domain in allowedDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            string normalized = domain.Trim().TrimStart('.').ToLowerInvariant();

            string hostLower = host.ToLowerInvariant();

            if (string.Equals(hostLower, normalized, StringComparison.Ordinal))
            {
                return true;
            }

            if (hostLower.EndsWith('.' + normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<SanctumResult> DenyPathAsync(
        string campaignId,
        string toolName,
        string requestedPath,
        string? resolvedPath,
        string workspaceRoot,
        string detail,
        int maxBreachCount,
        CancellationToken ct)
    {
        SanctumBreach breach = CreateBreach(
            campaignId,
            toolName,
            "PathEscape",
            detail,
            requestedPath,
            resolvedPath,
            workspaceRoot);

        await RecordBreachAsync(breach, maxBreachCount, ct).ConfigureAwait(false);

        return new SanctumResult
        {
            Allowed = false,
            DenyReason = detail,
            Breach = breach,
        };
    }

    private async Task<SanctumResult> DenyNetworkAsync(
        string campaignId,
        string toolName,
        string url,
        string detail,
        int maxBreachCount,
        CancellationToken ct)
    {
        SanctumBreach breach = CreateBreach(
            campaignId,
            toolName,
            "NetworkEgress",
            detail,
            requestedUrl: url);

        await RecordBreachAsync(breach, maxBreachCount, ct).ConfigureAwait(false);

        return new SanctumResult
        {
            Allowed = false,
            DenyReason = detail,
            Breach = breach,
        };
    }

    private async Task RecordBreachAsync(SanctumBreach breach, int maxBreachCount, CancellationToken ct)
    {
        LogBreach(breach);

        SanctumBreachRecord record = new(
            Id: string.Empty,
            CampaignId: breach.CampaignId,
            OccurredAt: breach.Timestamp,
            ToolName: breach.ToolName,
            BreachType: breach.BreachType,
            Description: breach.Detail,
            Details: BuildDetails(breach));

        try
        {
            await breachRepository.RecordAsync(record, maxBreachCount, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Persistence failure must not break tool-invocation enforcement: the synthetic
            // denial has already been decided and is returned regardless of write outcome.
            logger.LogError(
                ex,
                "Failed to persist Sanctum breach {BreachType} for campaign {CampaignId}.",
                breach.BreachType,
                breach.CampaignId);
        }
    }

    private static SanctumBreachDetails? BuildDetails(SanctumBreach breach)
    {
        if (breach.RequestedPath is null
            && breach.ResolvedPath is null
            && breach.WorkspaceRoot is null
            && breach.RequestedUrl is null)
        {
            return null;
        }

        return new SanctumBreachDetails(
            RequestedPath: breach.RequestedPath,
            ResolvedPath: breach.ResolvedPath,
            WorkspaceRoot: breach.WorkspaceRoot,
            RequestedUrl: breach.RequestedUrl,
            ToolArguments: null,
            LimitValue: null,
            ActualValue: null);
    }

    private void LogBreach(SanctumBreach breach)
    {
        logger.LogWarning(
            "Sanctum breach {BreachType} for campaign {CampaignId} tool {ToolName}: {Detail}",
            breach.BreachType,
            breach.CampaignId,
            breach.ToolName,
            breach.Detail);
    }

    private static SanctumBreach CreateBreach(
        string campaignId,
        string toolName,
        string breachType,
        string detail,
        string? requestedPath = null,
        string? resolvedPath = null,
        string? workspaceRoot = null,
        string? requestedUrl = null) =>
        new()
        {
            BreachId = Guid.NewGuid().ToString(),
            CampaignId = campaignId,
            ToolName = toolName,
            BreachType = breachType,
            Detail = detail,
            RequestedPath = requestedPath,
            ResolvedPath = resolvedPath,
            WorkspaceRoot = workspaceRoot,
            RequestedUrl = requestedUrl,
            Timestamp = DateTimeOffset.UtcNow,
        };

    private static SanctumResult AllowedResult() => new() { Allowed = true };

}
