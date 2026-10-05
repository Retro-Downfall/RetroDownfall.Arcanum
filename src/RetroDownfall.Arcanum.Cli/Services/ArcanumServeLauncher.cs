using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Coordination;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Cli.Services;

internal sealed class ArcanumServeLauncher(
    IOptionsMonitor<ArcanumSettings> settingsMonitor,
    ArcanumApiCredentialLease credentialLease,
    ICliEnvironment cliEnvironment,
    IServeProcessLauncher processLauncher,
    ISecureStorageNotice secureStorageNotice,
    ILogger<ArcanumServeLauncher> logger) : IArcanumServeLauncher
{
    internal const string AutoLaunchedEnvVar = "ARCANUM_AUTO_LAUNCHED";

    internal const string NoAutoServeEnvVar = "ARCANUM_NO_AUTO_SERVE";

    internal const string DevLauncherEnvVar = "ARCANUM_DEV_LAUNCHER";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan PollDeadline = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan ExistingHostRetryBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Test seam: when set, shortens the post-spawn poll window so timeout cases stay fast in CI.
    /// </summary>
    internal static TimeSpan? TestPollDeadline { get; set; }

    internal static string LaunchLockPath =>
        Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "arcanum.serve.lock");

    internal static string BootstrapLogPath =>
        Path.Combine(
            ArcanumPaths.GrimoireDirectory,
            "logs",
            "auto-serve-bootstrap.log");

    public async Task<ServeLaunchResult> EnsureRunningAsync(
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        if (!ShouldAutoServe())
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.LaunchDisabled,
                HealthProbeState.NotAttempted,
                stopwatch.Elapsed,
                null,
                "Auto-start disabled (non-interactive, redirected, or ARCANUM_NO_AUTO_SERVE=1).");
        }

        ApiCredentialLeaseResult presence = await credentialLease
            .ResolveAsync(ProbeTimeout, cancellationToken)
            .ConfigureAwait(false);

        ServeLaunchResult? existingHost = await ResolveExistingHostAsync(
                presence,
                stopwatch,
                cancellationToken)
            .ConfigureAwait(false);

        if (existingHost is not null)
        {
            return existingHost;
        }

        ArcanumSettings settings = settingsMonitor.CurrentValue;

        if (ListenAnySecurityPolicy.RequiresInteractiveConfirmation(
                settings.Host.ListenAny))
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                presence.ProbeState,
                stopwatch.Elapsed,
                null,
                "ListenAny requires acknowledgement — run `arcanum serve` manually once to acknowledge, or set ARCANUM_LISTEN_ANY_ACK=1 if intentional.");
        }

        // Two clients started together would both see no listener and both spawn a host. The lock makes
        // probe-and-spawn one step across processes, and it is held until this launch has finished, so a
        // second client waits for the host the first one started instead of racing it for the PID file.
        using LaunchLockHold launchLock = await AcquireLaunchLockAsync(cancellationToken)
            .ConfigureAwait(false);

        if (launchLock.TimedOut)
        {
            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                HealthProbeState.Timeout,
                stopwatch.Elapsed,
                BootstrapLogPath,
                $"Timed out waiting for another arcanum process that is starting the host. Check {BootstrapLogPath} and run `arcanum doctor`.");
        }

        if (launchLock.Waited)
        {
            // Whoever held the lock may have started the host in the meantime.
            presence = await credentialLease
                .ResolveAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            existingHost = await ResolveExistingHostAsync(
                    presence,
                    stopwatch,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existingHost is not null)
            {
                return existingHost;
            }
        }

        (string executable, IReadOnlyList<string> arguments) =
            ResolveServeLaunch();

        try
        {
            secureStorageNotice.ExplainBeforeHostBootstrap();

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
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to spawn arcanum serve.");

            return new ServeLaunchResult(
                ServeLaunchStatus.Failed,
                presence.ProbeState,
                stopwatch.Elapsed,
                BootstrapLogPath,
                $"Failed to start arcanum serve: {exception.Message}. Run `arcanum doctor`.");
        }

        return await PollUntilReadyAsync(
                stopwatch,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A result when something is already answering on the endpoint (running, still bootstrapping, or a
    /// failure that must not be spawned over); null when nothing listens and a host may be started.
    /// </summary>
    private async Task<ServeLaunchResult?> ResolveExistingHostAsync(
        ApiCredentialLeaseResult presence,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (presence.IsVerified)
        {
            return Success(
                ServeLaunchStatus.AlreadyRunning,
                stopwatch,
                logPath: null);
        }

        if (presence.ProbeState == HealthProbeState.UnhealthyStatus)
        {
            presence = await RetryExistingHostAsync(cancellationToken)
                .ConfigureAwait(false);

            if (presence.IsVerified)
            {
                return Success(
                    ServeLaunchStatus.AlreadyRunning,
                    stopwatch,
                    logPath: null);
            }

            return FailureFromPresence(
                presence,
                stopwatch,
                logPath: null);
        }

        return IsNoListener(presence.ProbeState)
            ? null
            : FailureFromPresence(
                presence,
                stopwatch,
                logPath: null);
    }

    /// <summary>
    /// Takes the owner-only cross-process launch lock, waiting while another client holds it. A lock that
    /// cannot be created safely does not stop the launch: the host's own PID-file claim is still the
    /// authority that admits exactly one host, so the launcher proceeds unserialised and says so.
    /// </summary>
    private async Task<LaunchLockHold> AcquireLaunchLockAsync(
        CancellationToken cancellationToken)
    {
        Stopwatch wait = Stopwatch.StartNew();

        TimeSpan deadline = TestPollDeadline ?? PollDeadline;

        bool waited = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RetainedExclusiveFileLockAcquisitionResult attempt;

            try
            {
                attempt = RetainedExclusiveFileLock.Acquire(LaunchLockPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    exception,
                    "Could not take the serve launch lock; starting without cross-process serialisation.");

                return new LaunchLockHold(null, waited, TimedOut: false);
            }

            switch (attempt.Disposition)
            {
                case RetainedExclusiveFileLockAcquisitionDisposition.Acquired:
                    return new LaunchLockHold(attempt.Lock, waited, TimedOut: false);

                case RetainedExclusiveFileLockAcquisitionDisposition.Unsafe:
                    logger.LogWarning(
                        "The serve launch lock at {Path} could not be used safely; starting without cross-process serialisation.",
                        LaunchLockPath);

                    return new LaunchLockHold(null, waited, TimedOut: false);

                default:
                    waited = true;

                    if (wait.Elapsed >= deadline)
                    {
                        return new LaunchLockHold(null, waited, TimedOut: true);
                    }

                    await Task.Delay(PollInterval, cancellationToken)
                        .ConfigureAwait(false);

                    break;
            }
        }
    }

    private readonly record struct LaunchLockHold(
        RetainedExclusiveFileLock? Lock,
        bool Waited,
        bool TimedOut) : IDisposable
    {
        public void Dispose() => Lock?.Dispose();
    }

    private async Task<ApiCredentialLeaseResult> RetryExistingHostAsync(
        CancellationToken cancellationToken)
    {
        Stopwatch retry = Stopwatch.StartNew();

        ApiCredentialLeaseResult last =
            ApiCredentialLeaseResult.Transient(
                HealthProbeState.UnhealthyStatus,
                "Arcanum is starting but its credential proof is not ready yet.");

        while (retry.Elapsed < ExistingHostRetryBudget)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(PollInterval, cancellationToken)
                .ConfigureAwait(false);

            last = await credentialLease
                .ResolveAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (last.IsVerified
                || last.ProbeState != HealthProbeState.UnhealthyStatus)
            {
                return last;
            }
        }

        return last;
    }

    private async Task<ServeLaunchResult> PollUntilReadyAsync(
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Stopwatch poll = Stopwatch.StartNew();

        TimeSpan deadline = TestPollDeadline ?? PollDeadline;

        ApiCredentialLeaseResult last =
            ApiCredentialLeaseResult.Transient(
                HealthProbeState.ConnectionRefused,
                "Arcanum has not opened its local endpoint yet.");

        while (poll.Elapsed < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = await credentialLease
                .ResolveAsync(ProbeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (last.IsVerified)
            {
                return Success(
                    ServeLaunchStatus.Started,
                    stopwatch,
                    BootstrapLogPath);
            }

            if (last.CredentialStatus is not null)
            {
                return FailureFromPresence(
                    last,
                    stopwatch,
                    BootstrapLogPath);
            }

            if (last.ProbeState is HealthProbeState.TlsFailure
                or HealthProbeState.UnexpectedResponder)
            {
                return FailureFromPresence(
                    last,
                    stopwatch,
                    BootstrapLogPath);
            }

            await Task.Delay(PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ServeLaunchResult(
            ServeLaunchStatus.Failed,
            last.ProbeState == HealthProbeState.NotAttempted
                ? HealthProbeState.Timeout
                : last.ProbeState,
            stopwatch.Elapsed,
            BootstrapLogPath,
            $"Timed out waiting for arcanum serve. Check {BootstrapLogPath} and run `arcanum doctor`.");
    }

    private static ServeLaunchResult Success(
        ServeLaunchStatus status,
        Stopwatch stopwatch,
        string? logPath) =>
        new(
            status,
            HealthProbeState.Healthy,
            stopwatch.Elapsed,
            logPath,
            null);

    private static ServeLaunchResult FailureFromPresence(
        ApiCredentialLeaseResult presence,
        Stopwatch stopwatch,
        string? logPath)
    {
        ServeLaunchStatus status = presence.CredentialStatus is null
            ? ServeLaunchStatus.Failed
            : ServeLaunchStatus.AuthFailed;

        string guidance = ArcanumApiCredentialFailureMapper
            .ToError(presence)
            .Message;

        return new ServeLaunchResult(
            status,
            presence.ProbeState,
            stopwatch.Elapsed,
            logPath,
            guidance);
    }

    private static bool IsNoListener(HealthProbeState state) =>
        state is HealthProbeState.ConnectionRefused
            or HealthProbeState.NetworkUnreachable
            or HealthProbeState.DnsFailure;

    private bool ShouldAutoServe()
    {
        if (!cliEnvironment.IsInteractive)
        {
            return false;
        }

        string? disabled =
            Environment.GetEnvironmentVariable(NoAutoServeEnvVar);

        return string.IsNullOrWhiteSpace(disabled)
            || (!string.Equals(
                    disabled.Trim(),
                    "1",
                    StringComparison.Ordinal)
                && !string.Equals(
                    disabled.Trim(),
                    bool.TrueString,
                    StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// The empty <see cref="Assembly.Location"/> a single-file or Native AOT image reports is the
    /// expected reading here, not a defect: it is only consulted to re-launch through
    /// <c>dotnet &lt;dll&gt; serve</c> during development, and the blank-check below falls through
    /// to <see cref="Environment.ProcessPath"/> for every published build.
    /// </remarks>
    [UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "Empty Location is handled: the blank check falls through to Environment.ProcessPath.")]
    internal static (string Executable, IReadOnlyList<string> Arguments)
        ResolveServeLaunch()
    {
        string? processPath = Environment.ProcessPath;

        string? entryLocation = Assembly.GetEntryAssembly()?.Location;

        bool forceDev = string.Equals(
            Environment.GetEnvironmentVariable(DevLauncherEnvVar),
            "1",
            StringComparison.Ordinal);

        bool looksLikeDotnet = processPath is not null
            && (string.Equals(
                    Path.GetFileNameWithoutExtension(processPath),
                    "dotnet",
                    StringComparison.OrdinalIgnoreCase)
                || forceDev);

        if (looksLikeDotnet
            && !string.IsNullOrWhiteSpace(entryLocation))
        {
            return ("dotnet", [entryLocation, "serve"]);
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException(
                "Cannot resolve arcanum executable path for auto-serve.");
        }

        return (processPath, ["serve"]);
    }
}
