using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Sanctum;
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
/// </remarks>
public sealed class SanctumGuard(
    ICampaignRepository campaignRepository,
    SanctumBreachStore breachStore,
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
            return DenyPath(
                campaignId,
                toolName,
                requestedPath,
                null,
                campaign.Path,
                "The campaign workspace path could not be resolved.");
        }

        string candidateFull;

        try
        {
            candidateFull = Path.GetFullPath(requestedPath.Trim());
        }
        catch (Exception)
        {
            return DenyPath(
                campaignId,
                toolName,
                requestedPath,
                null,
                workspaceRoot,
                $"Invalid path for {operationType}.");
        }

        if (IsUnderAllowedRoots(workspaceRoot, candidateFull, config.AllowedPaths))
        {
            return AllowedResult();
        }

        return DenyPath(
            campaignId,
            toolName,
            requestedPath,
            candidateFull,
            workspaceRoot,
            $"Path for {operationType} would leave the campaign workspace.");
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
            return DenyNetwork(campaignId, toolName, url, "Network egress is denied by this Sanctum.");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return DenyNetwork(campaignId, toolName, url, "The outbound URL is not a valid HTTP or HTTPS address.");
        }

        string host = uri.Host;

        if (await IsHostAllowedAsync(host, config.AllowedDomains, ct).ConfigureAwait(false))
        {
            return AllowedResult();
        }

        return DenyNetwork(
            campaignId,
            toolName,
            url,
            $"Host '{host}' is not in the Sanctum allowed domain list.");
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

            RecordBreach(breach);

            return new SanctumResult
            {
                Allowed = false,
                DenyReason = breach.Detail,
                Breach = breach,
            };
        }

        return AllowedResult();
    }

    public Task<IReadOnlyList<SanctumBreach>> GetBreachesAsync(
        string campaignId,
        int limit = 100,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int clamped = Math.Clamp(limit, 1, 1000);

        IReadOnlyList<SanctumBreach> snapshot = breachStore.GetSnapshot(campaignId, clamped);

        SanctumBreach[] redacted = snapshot.Select(RedactBreachForApi).ToArray();

        return Task.FromResult<IReadOnlyList<SanctumBreach>>(redacted);
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

    private static ResourceLimits ClampResourceLimits(ResourceLimits limits) =>
        limits with
        {
            MaxProcessMemoryMb = ArcanumSettingClamps.SanctumMaxProcessMemoryMb(limits.MaxProcessMemoryMb),
            MaxProcessCount = ArcanumSettingClamps.SanctumMaxProcessCount(limits.MaxProcessCount),
            MaxFileWriteMb = ArcanumSettingClamps.SanctumMaxFileWriteMb(limits.MaxFileWriteMb),
            ProcessTimeoutSeconds = ArcanumSettingClamps.SanctumProcessTimeoutSeconds(limits.ProcessTimeoutSeconds),
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

        RecordBreach(breach);

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
            RequestedPath = RedactPath(breach.RequestedPath),
            ResolvedPath = RedactPath(breach.ResolvedPath),
            WorkspaceRoot = RedactPath(breach.WorkspaceRoot),
        };

    private static string? RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetFileName(path.Trim());
        }
        catch (Exception)
        {
            return "[redacted]";
        }
    }

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

    private SanctumResult DenyPath(
        string campaignId,
        string toolName,
        string requestedPath,
        string? resolvedPath,
        string workspaceRoot,
        string detail)
    {
        SanctumBreach breach = CreateBreach(
            campaignId,
            toolName,
            "PathEscape",
            detail,
            requestedPath,
            resolvedPath,
            workspaceRoot);

        RecordBreach(breach);

        return new SanctumResult
        {
            Allowed = false,
            DenyReason = detail,
            Breach = breach,
        };
    }

    private SanctumResult DenyNetwork(string campaignId, string toolName, string url, string detail)
    {
        SanctumBreach breach = CreateBreach(
            campaignId,
            toolName,
            "NetworkEgress",
            detail,
            requestedUrl: url);

        RecordBreach(breach);

        return new SanctumResult
        {
            Allowed = false,
            DenyReason = detail,
            Breach = breach,
        };
    }

    private void RecordBreach(SanctumBreach breach)
    {
        breachStore.Record(breach);

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
