using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

[ExcludeFromCodeCoverage] // Reason: Windows Service interop, platform-bound
public sealed class WindowsDaemonManager : IDaemonManager
{
    internal const string ServiceName = "ArcanumDaemon";
    internal const string NotInstalledMessage = "ArcanumDaemon is not installed.";
    private const int ErrorAccessDenied = 5;

    private const int ErrorServiceDoesNotExist = 1060;

    private const int ErrorServiceNotActive = 1062;

    private const int ServiceStopped = 1;

    private const int ServiceStartPending = 2;

    private const int ServiceStopPending = 3;

    private const int ServiceRunning = 4;

    private const int ServiceContinuePending = 5;

    private const int ServicePausePending = 6;

    private const int ServicePaused = 7;
    private static readonly Error ElevationError = new(
        "DaemonElevationRequired",
        "Administrator privileges are required to manage Windows Services.");
    private static string DefaultScExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "sc.exe");
    private readonly IDaemonProcessRunner _runner;

    private readonly string _scExePath;

    public WindowsDaemonManager()
        : this(DaemonProcessRunner.Default, DefaultScExePath)
    {
    }

    internal WindowsDaemonManager(IDaemonProcessRunner runner, string scExePath)
    {
        _runner = runner;
        _scExePath = scExePath;
    }

    /// <summary>
    /// Refuses to register a Windows Service. <c>sc create</c> without an <c>obj=</c> account runs the service as
    /// LocalSystem, whose profile, API key and data directory are not the invoking user's, and a named account would
    /// need its password on the <c>sc.exe</c> command line. The remedy is a per-user Task Scheduler entry, which runs
    /// as the user with no stored credential. No <c>sc.exe</c> process is started.
    /// </summary>
    public Task<Result> InstallAsync(CancellationToken cancellationToken)
    {
        string processPath = string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? "<path to arcanum.exe>"
            : Environment.ProcessPath;
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"Arcanum does not install a Windows Service: one created without a user account runs as LocalSystem, whose profile, API key and data directory are not yours, so your own arcanum commands could not reach it. Register a per-user Task Scheduler task that runs the host as you at logon instead (Task Scheduler, or: schtasks /Create /TN Arcanum /SC ONLOGON /RL LIMITED /TR \"\\\"{processPath}\\\" serve\"). A service created by an earlier version is still reported by 'arcanum daemon status' and removed by 'arcanum daemon uninstall'.");

        return Task.FromResult(Result.Failure(new Error("DaemonWindowsServiceUnsupported", message)));
    }

    public async Task<Result> UninstallAsync(CancellationToken cancellationToken)
    {
        DaemonProcessOutcome stopOutcome = await RunScAsync(
            ["stop", ServiceName],
            cancellationToken).ConfigureAwait(false);
        if (stopOutcome.FatalError is { } fatalStop)
        {
            return Result.Failure(fatalStop);
        }

        if (IndicatesElevationDenied(stopOutcome.ExitCode, stopOutcome.StdErr))
        {
            return Result.Failure(ElevationError);
        }

        if (stopOutcome.ExitCode != 0
            && stopOutcome.ExitCode != ErrorServiceNotActive
            && stopOutcome.ExitCode != ErrorServiceDoesNotExist
            && !IndicatesServiceNotActive(stopOutcome.StdErr))
        {
            return Result.Failure(
                ToolError("DaemonScStop", "sc stop failed.", stopOutcome.StdErr, stopOutcome.ExitCode));
        }
        DaemonProcessOutcome deleteOutcome = await RunScAsync(
            ["delete", ServiceName],
            cancellationToken).ConfigureAwait(false);
        if (deleteOutcome.FatalError is { } fatalDelete)
        {
            return Result.Failure(fatalDelete);
        }

        if (IndicatesElevationDenied(deleteOutcome.ExitCode, deleteOutcome.StdErr))
        {
            return Result.Failure(ElevationError);
        }

        if (deleteOutcome.ExitCode != 0)
        {
            if (deleteOutcome.ExitCode == ErrorServiceDoesNotExist)
            {
                return Result.Success();
            }

            return Result.Failure(
                ToolError("DaemonScDelete", "sc delete failed.", deleteOutcome.StdErr, deleteOutcome.ExitCode));
        }

        return Result.Success();
    }

    public async Task<Result<string>> GetStatusAsync(CancellationToken cancellationToken)
    {
        DaemonProcessOutcome queryOutcome = await RunScAsync(
            ["query", ServiceName],
            cancellationToken).ConfigureAwait(false);
        if (queryOutcome.FatalError is { } fatal)
        {
            return Result<string>.Failure(fatal);
        }

        if (IndicatesElevationDenied(queryOutcome.ExitCode, queryOutcome.StdErr))
        {
            return Result<string>.Failure(ElevationError);
        }

        if (queryOutcome.ExitCode == ErrorServiceDoesNotExist
            || IndicatesServiceDoesNotExist(queryOutcome.StdErr, queryOutcome.ExitCode))
        {
            return Result<string>.Success(NotInstalledMessage);
        }

        if (queryOutcome.ExitCode != 0)
        {
            return Result<string>.Failure(
                ToolError("DaemonScQuery", "sc query failed.", queryOutcome.StdErr, queryOutcome.ExitCode));
        }

        if (!TryParseServiceStateCode(queryOutcome.StdOut, out int stateCode))
        {
            return Result<string>.Failure(
                new Error("DaemonStatusParse", "Could not parse sc query STATE numeric code."));
        }

        return Result<string>.Success(FormatStateMessage(stateCode));
    }

    private static string FormatStateMessage(int stateCode)
    {
        return stateCode switch
        {
            ServiceRunning => "ArcanumDaemon is running.",
            ServiceStopped => "ArcanumDaemon is installed but stopped.",
            ServiceStartPending => "ArcanumDaemon is starting (state code 2).",
            ServiceStopPending => "ArcanumDaemon is stopping (state code 3).",
            ServiceContinuePending => "ArcanumDaemon is resuming (state code 5).",
            ServicePausePending => "ArcanumDaemon is pausing (state code 6).",
            ServicePaused => "ArcanumDaemon is paused (state code 7).",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"ArcanumDaemon reports an unexpected service state code {stateCode}."),
        };
    }

    /// <summary>
    /// Parses <c>dwCurrentState</c> from the fixed <c>STATE</c> line in <c>sc query</c> output (numeric only; localized text after the code is ignored).
    /// </summary>
    private static bool TryParseServiceStateCode(string stdout, out int stateCode)
    {
        stateCode = 0;
        foreach (string raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (!line.StartsWith("STATE", StringComparison.Ordinal))
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || colon + 1 >= line.Length)
            {
                continue;
            }

            ReadOnlySpan<char> tail = line.AsSpan(colon + 1).TrimStart();
            int length = 0;
            while (length < tail.Length && char.IsAsciiDigit(tail[length]))
            {
                length++;
            }

            if (length == 0)
            {
                continue;
            }

            if (int.TryParse(tail[..length], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                stateCode = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool IndicatesServiceDoesNotExist(string stderr, int exitCode)
    {
        if (exitCode == ErrorServiceDoesNotExist)
        {
            return true;
        }

        return stderr.Contains("1060", StringComparison.Ordinal);
    }

    private static bool IndicatesServiceNotActive(string stderr)
    {
        return stderr.Contains("1062", StringComparison.Ordinal);
    }

    private static bool IndicatesElevationDenied(int exitCode, string stderr)
    {
        if (exitCode == ErrorAccessDenied)
        {
            return true;
        }

        return stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase);
    }

    private static Error ToolError(string code, string message, string stderr, int exitCode)
    {
        string trimmed = stderr.Trim();
        string suffix = string.IsNullOrEmpty(trimmed) ? $"Exit code {exitCode}." : trimmed;
        return new Error(code, $"{message} {suffix}".Trim());
    }

    private async Task<DaemonProcessOutcome> RunScAsync(string[] arguments, CancellationToken cancellationToken)
    {
        DaemonProcessOutcome outcome = await _runner
            .RunAsync(_scExePath, arguments, cancellationToken)
            .ConfigureAwait(false);
        return outcome.AccessDenied
            ? outcome with { FatalError = ElevationError }
            : outcome;
    }
}
