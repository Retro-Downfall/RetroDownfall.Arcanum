using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

internal sealed class ArcanumServeLauncher(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ISecretStore secretStore,
    ICliEnvironment cliEnvironment,
    IServeProcessLauncher processLauncher,
    ILogger<ArcanumServeLauncher> logger) : IArcanumServeLauncher
{

    internal const string AutoLaunchedEnvVar = "ARCANUM_AUTO_LAUNCHED";

    internal const string NoAutoServeEnvVar = "ARCANUM_NO_AUTO_SERVE";

    internal const string DevLauncherEnvVar = "ARCANUM_DEV_LAUNCHER";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PollDeadline = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan UnhealthyRetryBudget = TimeSpan.FromSeconds(3);

    private static readonly int UnauthorizedWithKeyFailAfter = 8;

    /// <summary>
    /// Test seam: when set, shortens the post-spawn poll window so timeout cases stay fast in CI.
    /// </summary>
    internal static TimeSpan? TestPollDeadline { get; set; }

    internal static string BootstrapLogPath =>
        Path.Combine(ArcanumPaths.GrimoireDirectory, "logs", "auto-serve-bootstrap.log");

    public async Task<ServeLaunchResult> EnsureRunningAsync(CancellationToken cancellationToken)
    {

        Stopwatch sw = Stopwatch.StartNew();

        if (!ShouldAutoServe())
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.LaunchDisabled,
                HealthProbeState.NotAttempted,
                sw.Elapsed,
                null,
                "Auto-start disabled (non-interactive, redirected, or ARCANUM_NO_AUTO_SERVE=1).");
        }

        ArcanumSettings settings = settingsMonitor.CurrentValue;

        Uri healthUrl = new(ArcanumLocalApiAddress.ResolveHealthProbeUrl(settings.Host));

        HttpClient client = httpClientFactory.CreateClient(ArcanumApiClient.RequestHttpClientName);

        string? apiKey = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

        HealthProbeResult probe = await ArcanumHealthProbe
            .ProbeAsync(client, healthUrl, apiKey, ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (probe.State == HealthProbeState.Healthy)
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.AlreadyRunning,
                probe.State,
                sw.Elapsed,
                null,
                null);
        }

        if (probe.State == HealthProbeState.Unauthorized)
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.AuthFailed,
                probe.State,
                sw.Elapsed,
                null,
                "Server reachable but this CLI cannot authenticate — run `arcanum key show` or re-authenticate.");
        }

        if (probe.State == HealthProbeState.UnhealthyStatus)
        {
            HealthProbeResult retried = await RetryUnhealthyAsync(
                    client,
                    healthUrl,
                    cancellationToken)
                .ConfigureAwait(false);

            if (retried.State == HealthProbeState.Healthy)
            {
                return new ServeLaunchResult(
                    ServeLaunchStatus.AlreadyRunning,
                    retried.State,
                    sw.Elapsed,
                    null,
                    null);
            }

            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                retried.State,
                sw.Elapsed,
                BootstrapLogPath,
                "Server is up but not healthy/ready. Run `arcanum doctor`.");
        }

        if (probe.State == HealthProbeState.TlsFailure)
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                probe.State,
                sw.Elapsed,
                null,
                "Something answered at the address with a TLS/cert problem (untrusted cert, hostname/SAN mismatch, or a non-Arcanum service on the port). Run `arcanum doctor`. Do not auto-start.");
        }

        if (probe.State == HealthProbeState.Timeout)
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                probe.State,
                sw.Elapsed,
                null,
                "Probe timed out — a server may be stuck or listening but not responding. Run `arcanum doctor`.");
        }

        if (probe.State is not (
            HealthProbeState.ConnectionRefused
            or HealthProbeState.NetworkUnreachable
            or HealthProbeState.DnsFailure))
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                probe.State,
                sw.Elapsed,
                BootstrapLogPath,
                "Unexpected health probe state. Run `arcanum doctor`.");
        }

        if (ListenAnySecurityPolicy.RequiresInteractiveConfirmation(settings.Host.ListenAny))
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                probe.State,
                sw.Elapsed,
                null,
                "ListenAny requires acknowledgement — run `arcanum serve` manually once to acknowledge, or set ARCANUM_LISTEN_ANY_ACK=1 if intentional.");
        }

        (string executable, IReadOnlyList<string> arguments) = ResolveServeLaunch();

        try
        {
            _ = await processLauncher
                .StartServeAsync(
                    new ServeProcessStartOptions(
                        executable,
                        arguments,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [AutoLaunchedEnvVar] = "1",
                        },
                        Environment.CurrentDirectory),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to spawn arcanum serve.");

            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                probe.State,
                sw.Elapsed,
                BootstrapLogPath,
                $"Failed to start arcanum serve: {ex.Message}. Run `arcanum doctor`.");
        }

        return await PollUntilReadyAsync(client, healthUrl, sw, cancellationToken).ConfigureAwait(false);

    }

    private async Task<HealthProbeResult> RetryUnhealthyAsync(
        HttpClient client,
        Uri healthUrl,
        CancellationToken cancellationToken)
    {

        Stopwatch budget = Stopwatch.StartNew();

        HealthProbeResult last = new(HealthProbeState.UnhealthyStatus, 503, TimeSpan.Zero, null);

        while (budget.Elapsed < UnhealthyRetryBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? key = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

            last = await ArcanumHealthProbe
                .ProbeAsync(client, healthUrl, key, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (last.State is HealthProbeState.Healthy or HealthProbeState.Unauthorized)
            {
                return last;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return last;

    }

    private async Task<ServeLaunchResult> PollUntilReadyAsync(
        HttpClient client,
        Uri healthUrl,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {

        Stopwatch poll = Stopwatch.StartNew();

        int unauthorizedWithKeyStreak = 0;

        TimeSpan deadline = TestPollDeadline ?? PollDeadline;

        while (poll.Elapsed < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? key = await secretStore.GetApiKeyAsync().ConfigureAwait(false);

            HealthProbeResult probe = await ArcanumHealthProbe
                .ProbeAsync(client, healthUrl, key, ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (probe.State == HealthProbeState.Healthy)
            {
                return new ServeLaunchResult(
                    ServeLaunchStatus.Started,
                    probe.State,
                    sw.Elapsed,
                    BootstrapLogPath,
                    null);
            }

            if (probe.State == HealthProbeState.Unauthorized)
            {
                if (key is null)
                {
                    // First-run race: key not yet written by the spawned server.
                    unauthorizedWithKeyStreak = 0;
                }
                else
                {
                    unauthorizedWithKeyStreak++;

                    if (unauthorizedWithKeyStreak >= UnauthorizedWithKeyFailAfter)
                    {
                        return new ServeLaunchResult(
                            ServeLaunchStatus.AuthFailed,
                            probe.State,
                            sw.Elapsed,
                            BootstrapLogPath,
                            "Server started but rejects this CLI's API key — run `arcanum key show` or re-authenticate.");
                    }
                }
            }
            else
            {
                unauthorizedWithKeyStreak = 0;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return new ServeLaunchResult(
            ServeLaunchStatus.Failed,
            HealthProbeState.Timeout,
            sw.Elapsed,
            BootstrapLogPath,
            $"Timed out waiting for arcanum serve. Check {BootstrapLogPath} and run `arcanum doctor`.");

    }

    private bool ShouldAutoServe()
    {

        // Trust ICliEnvironment.IsInteractive alone: CliEnvironment already encodes
        // !stdin && !stdout redirected ("Interactive + not redirected"). Re-checking
        // Console.Is*Redirected here duplicates that gate and breaks unit tests (xUnit
        // redirects stdout even when a fake interactive ICliEnvironment is injected).
        if (!cliEnvironment.IsInteractive)
        {
            return false;
        }

        string? disabled = Environment.GetEnvironmentVariable(NoAutoServeEnvVar);

        if (!string.IsNullOrWhiteSpace(disabled)
            && (string.Equals(disabled.Trim(), "1", StringComparison.Ordinal)
                || string.Equals(disabled.Trim(), bool.TrueString, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;

    }

    internal static (string Executable, IReadOnlyList<string> Arguments) ResolveServeLaunch()
    {

        string? processPath = Environment.ProcessPath;

#pragma warning disable IL3000 // Location is empty in single-file; only used for dotnet-run (non-single-file) launches.
        string? entryLocation = Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000

        bool forceDev = string.Equals(
            Environment.GetEnvironmentVariable(DevLauncherEnvVar),
            "1",
            StringComparison.Ordinal);

        bool looksLikeDotnet = processPath is not null
            && (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase)
                || forceDev);

        if (looksLikeDotnet && !string.IsNullOrWhiteSpace(entryLocation))
        {
            return ("dotnet", [entryLocation, "serve"]);
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Cannot resolve arcanum executable path for auto-serve.");
        }

        return (processPath, ["serve"]);

    }

}
