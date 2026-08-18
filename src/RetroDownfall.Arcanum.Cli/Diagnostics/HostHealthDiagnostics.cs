using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Models;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Cli;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Security;

namespace RetroDownfall.Arcanum.Cli.Diagnostics;

/// <summary>
/// The running host's own per-subsystem verdicts, relayed into the doctor report.
///
/// <para>The host already computes a status for Grimoire, MCP, providers, embeddings, file
/// encryption, the tool-child sandbox, workspace checks, and Conclave — subsystems the CLI either
/// cannot see at all (their services are registered only in the host container) or could only
/// re-implement differently. Relaying is strictly better than a second implementation that drifts.</para>
///
/// <para>An unreachable host is <see cref="DoctorOutcome.Unavailable"/>, not a failure: every local
/// check still ran, and a diagnostic that failed whenever <c>arcanum serve</c> was not running would
/// be useless in exactly the case it exists for. Talking to <c>localhost</c> is not the outbound
/// egress <c>--include-network</c> gates, so this check is not network-gated.</para>
/// </summary>
public sealed class HostHealthComponentsCheck(
    IOptions<ArcanumSettings> options,
    IHttpClientFactory httpClientFactory,
    ISecretStore secretStore) : IDoctorCheck
{

    public const string CheckId = "host.health_components";

    public string Id => CheckId;

    public string Name => "Host subsystem health";

    public DoctorSubsystem Subsystem => DoctorSubsystem.Host;

    public bool RequiresNetwork => false;

    public async Task<DoctorFinding> InspectAsync(CancellationToken cancellationToken)
    {

        HostSettings host = options.Value.Host;

        int timeoutSeconds = ArcanumSettingClamps.DoctorHealthTimeoutSeconds(
            ArcanumRuntimeDefaults.CliDoctorHealthTimeoutSeconds);

        HttpClient client = httpClientFactory.CreateClient(ArcanumApiClient.RequestHttpClientName);

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        HealthProbeResult probe = await ArcanumHealthProbe
            .ProbeAsync(
                client,
                new Uri(ArcanumLocalApiAddress.ResolveHealthProbeUrl(host)),
                apiKey,
                TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken)
            .ConfigureAwait(false);

        return Describe(probe);

    }

    internal static DoctorFinding Describe(HealthProbeResult probe)
    {

        if (probe.State == HealthProbeState.Unauthorized)
        {

            return new DoctorFinding(
                DoctorOutcome.Unhealthy,
                "The host answered but rejected this machine's API key, so its subsystem health "
                + "could not be read.",
                [
                    new DoctorRemedy(
                        "host.repair_api_key",
                        null,
                        DoctorRemedyCommands.KeySet,
                        "Store the master API key this host expects. 'arcanum key show' prints the current one to stderr."),
                ]);

        }

        // A host that answers 503 is running and unhealthy — the single most informative state this
        // check has. Reporting it as "not running" would hide every component verdict at exactly the
        // moment they matter, so only a genuine no-answer takes the unavailable path.
        if (probe.State == HealthProbeState.UnhealthyStatus)
        {

            return new DoctorFinding(
                DoctorOutcome.Unhealthy,
                $"The host is running but reports itself unhealthy (HTTP {probe.StatusCode}). "
                + "Its own health report names the failing components.",
                [
                    new DoctorRemedy(
                        "host.inspect_components",
                        null,
                        DoctorRemedyCommands.Lore,
                        "Inspect the host's own health report for the failing components."),
                ]);

        }

        // Something is holding the port and answering as no Arcanum host would. "Start the host" is
        // the one remedy that cannot work here — a second host cannot bind a port another process
        // already owns — so this state gets its own finding rather than the no-answer one.
        if (probe.State == HealthProbeState.UnexpectedResponder)
        {

            return new DoctorFinding(
                DoctorOutcome.Unhealthy,
                "Something is listening at the host address but answered as no Arcanum host would "
                + $"({probe.Error ?? "no detail"}). A foreign service is likely holding the port.",
                [
                    new DoctorRemedy(
                        "host.free_port",
                        null,
                        DoctorRemedyCommands.ConfigEdit,
                        "Free the configured port, or point 'Arcanum:Host:Port' at one nothing else is using."),
                ]);

        }

        if (probe.State != HealthProbeState.Healthy)
        {

            return new DoctorFinding(
                DoctorOutcome.Unavailable,
                "The host did not answer, so its per-subsystem health could not be read. Every local "
                + "check still ran.",
                [
                    new DoctorRemedy(
                        "host.start",
                        null,
                        DoctorRemedyCommands.Serve,
                        "Start the host, then re-run 'arcanum doctor' to include its subsystem verdicts."),
                ]);

        }

        IReadOnlyList<HealthComponentDto> components = probe.Components ?? [];

        if (components.Count == 0)
        {

            return new DoctorFinding(
                DoctorOutcome.Degraded,
                "The host is reachable but reported no subsystem components.");

        }

        IReadOnlyList<HealthComponentDto> unhealthy =
            [.. components.Where(static component => component.Status == HealthStatus.Unhealthy)];

        IReadOnlyList<HealthComponentDto> degraded =
            [.. components.Where(static component => component.Status == HealthStatus.Degraded)];

        if (unhealthy.Count == 0 && degraded.Count == 0)
        {

            return new DoctorFinding(
                DoctorOutcome.Healthy,
                $"All {components.Count} host subsystem(s) report healthy.");

        }

        string detail = string.Join(
            "; ",
            unhealthy.Concat(degraded).Select(static component =>
                $"{component.Name}={DescribeStatus(component.Status)}"
                + (string.IsNullOrWhiteSpace(component.Detail) ? string.Empty : $" ({component.Detail})")));

        return new DoctorFinding(
            unhealthy.Count > 0 ? DoctorOutcome.Unhealthy : DoctorOutcome.Degraded,
            $"{unhealthy.Count} unhealthy and {degraded.Count} degraded host subsystem(s): {detail}",
            [
                new DoctorRemedy(
                    "host.inspect_components",
                    null,
                    DoctorRemedyCommands.Lore,
                    "Inspect the host's own health report for the named components."),
            ]);

    }

    private static string DescribeStatus(HealthStatus status) => status switch
    {
        HealthStatus.Unhealthy => "unhealthy",
        HealthStatus.Degraded => "degraded",
        _ => "healthy",
    };

}
