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

    private const int ErrorServiceLogonFailed = 1069;

    private const int ErrorServiceExists = 1073;

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

    public bool RequiresServiceAccount => true;

    /// <summary>
    /// Registers the daemon as a Windows Service that runs under the account the caller supplied, and starts it.
    /// <c>sc create</c> without an <c>obj=</c> account runs the service as LocalSystem, whose profile, API key and
    /// data directory are not the invoking user's, so a request with no account, or one that names LocalSystem under
    /// any of its aliases, is refused before any <c>sc.exe</c> process starts and the message directs the person to a
    /// per-user Task Scheduler entry, which runs as them with no stored credential.
    /// <para>
    /// The password reaches <c>sc.exe</c> as the value of its own <c>password=</c> argument, because <c>sc.exe</c>
    /// has no other channel; Arcanum's own command line never carries it and it is never written to a message. A
    /// service that was created but did not start is deleted again, on a token that cannot be cancelled, so a
    /// failed or cancelled install never leaves a service with a stored credential behind.
    /// </para>
    /// </summary>
    public async Task<Result> InstallAsync(DaemonInstallRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ServiceAccount is not { } account)
        {
            return Result.Failure(ServiceAccountRequiredError());
        }

        Result accepted = ValidateServiceAccount(account);

        if (accepted.IsFailure)
        {
            return accepted;
        }

        string? processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return Result.Failure(new Error("DaemonProcessPath", "Could not resolve the current executable path."));
        }

        string servicePath = string.Create(CultureInfo.InvariantCulture, $"\"{processPath}\" serve");

        DaemonProcessOutcome createOutcome = await RunScAsync(
            [
                "create",
                ServiceName,
                "binPath=",
                servicePath,
                "start=",
                "auto",
                "obj=",
                account.AccountName.Trim(),
                "password=",
                account.Password,
            ],
            cancellationToken).ConfigureAwait(false);

        if (createOutcome.FatalError is { } fatalCreate)
        {
            return Result.Failure(fatalCreate);
        }

        if (IndicatesElevationDenied(createOutcome.ExitCode, createOutcome.StdErr))
        {
            return Result.Failure(ElevationError);
        }

        if (createOutcome.ExitCode == ErrorServiceExists)
        {
            return Result.Failure(
                new Error(
                    "DaemonScCreate",
                    "ArcanumDaemon already exists. Run 'arcanum daemon uninstall' to remove it, then install again."));
        }

        if (createOutcome.ExitCode != 0)
        {
            return Result.Failure(
                ToolError(
                    "DaemonScCreate",
                    "sc create failed.",
                    Redact(createOutcome.StdErr, account.Password),
                    createOutcome.ExitCode));
        }

        DaemonProcessOutcome startOutcome;

        try
        {
            startOutcome = await RunScAsync(["start", ServiceName], cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = await RemoveCreatedServiceAsync().ConfigureAwait(false);

            throw;
        }

        if (startOutcome.FatalError is { } fatalStart)
        {
            return await FailAfterRollbackAsync(fatalStart).ConfigureAwait(false);
        }

        if (IndicatesElevationDenied(startOutcome.ExitCode, startOutcome.StdErr))
        {
            return await FailAfterRollbackAsync(ElevationError).ConfigureAwait(false);
        }

        if (startOutcome.ExitCode == ErrorServiceLogonFailed)
        {
            return await FailAfterRollbackAsync(
                new Error(
                    "DaemonScStart",
                    "sc start failed: Windows refused to log the service on as the account you gave (error 1069). "
                    + "Check the password, and that the account may log on as a service (Local Security Policy, "
                    + "User Rights Assignment, 'Log on as a service').")).ConfigureAwait(false);
        }

        if (startOutcome.ExitCode != 0)
        {
            return await FailAfterRollbackAsync(
                ToolError(
                    "DaemonScStart",
                    "sc start failed.",
                    Redact(startOutcome.StdErr, account.Password),
                    startOutcome.ExitCode)).ConfigureAwait(false);
        }

        return Result.Success();
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

    /// <summary>
    /// The names Windows accepts for the LocalSystem account. A service that runs as any of them is the problem the
    /// refusal exists for, so naming one is the same as naming no account.
    /// </summary>
    private static bool IsLocalSystemAlias(string accountName)
    {
        string normalized = accountName.Trim();

        if (normalized.StartsWith(@".\", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(@"NT AUTHORITY\LocalSystem", StringComparison.OrdinalIgnoreCase);
    }

    private static Result ValidateServiceAccount(DaemonServiceCredential account)
    {
        if (string.IsNullOrWhiteSpace(account.AccountName) || account.AccountName.Any(char.IsControl))
        {
            return Result.Failure(
                new Error(
                    "DaemonServiceAccountInvalid",
                    @"The service account name is empty or contains control characters. Give the account as DOMAIN\user, .\user or a user principal name."));
        }

        if (IsLocalSystemAlias(account.AccountName))
        {
            return Result.Failure(
                new Error(
                    "DaemonServiceAccountInvalid",
                    "A service that runs as LocalSystem has a different profile, data directory and API key from your own arcanum commands, so Arcanum does not install one. Give your own account, or register a per-user Task Scheduler task instead. "
                    + TaskSchedulerRemedy()));
        }

        if (string.IsNullOrEmpty(account.Password))
        {
            return Result.Failure(
                new Error(
                    "DaemonServiceAccountInvalid",
                    "The service account needs its password: Windows cannot start a service under a user account without one."));
        }

        return Result.Success();
    }

    private static Error ServiceAccountRequiredError() =>
        new(
            "DaemonServiceAccountRequired",
            "Arcanum does not install a Windows Service without a user account: one created without an account runs as LocalSystem, whose profile, API key and data directory are not yours, so your own arcanum commands could not reach it. Install it under your own account (arcanum daemon install asks for the account and its password), or register a per-user Task Scheduler task instead. "
            + TaskSchedulerRemedy()
            + " A service created by an earlier version is still reported by 'arcanum daemon status' and removed by 'arcanum daemon uninstall'.");

    private static string TaskSchedulerRemedy()
    {
        string processPath = string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? "<path to arcanum.exe>"
            : Environment.ProcessPath;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"A per-user task runs the host as you at logon with no stored password (Task Scheduler, or: schtasks /Create /TN Arcanum /SC ONLOGON /RL LIMITED /TR \"\\\"{processPath}\\\" serve\").");
    }

    /// <summary>
    /// Keeps the password out of anything echoed back from <c>sc.exe</c>.
    /// </summary>
    private static string Redact(string text, string password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "<redacted>", StringComparison.Ordinal);

    private async Task<Result> FailAfterRollbackAsync(Error error)
    {
        Result rolledBack = await RemoveCreatedServiceAsync().ConfigureAwait(false);

        string suffix = rolledBack.IsSuccess
            ? " The service was removed again."
            : " The service could not be removed again; run 'arcanum daemon uninstall'.";

        return Result.Failure(new Error(error.Code, error.Message + suffix));
    }

    /// <summary>
    /// Deletes the service this install just created. It runs after the create has taken effect, so it uses a token
    /// that cannot be cancelled: a cancelled install must not strand a service that stores the account's password.
    /// </summary>
    private async Task<Result> RemoveCreatedServiceAsync()
    {
        DaemonProcessOutcome deleted = await RunScAsync(["delete", ServiceName], CancellationToken.None)
            .ConfigureAwait(false);

        return deleted.FatalError is null && deleted.ExitCode is 0 or ErrorServiceDoesNotExist
            ? Result.Success()
            : Result.Failure(new Error("DaemonScDelete", "sc delete failed."));
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
