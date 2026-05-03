using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using RetroDownfall.Arcanum.Core.Hosting;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

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
    private static string ScExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "sc.exe");
    public async Task<Result> InstallAsync(CancellationToken cancellationToken)
    {
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return Result.Failure(new Error("DaemonProcessPath", "Could not resolve the current executable path."));
        }

        string binPathArgument = string.Create(
            CultureInfo.InvariantCulture,
            $"binPath= \"\\\"{processPath}\\\" serve\"");
        RunOutcome createOutcome = await RunScAsync(
            ["create", ServiceName, binPathArgument, "start= auto"],
            cancellationToken).ConfigureAwait(false);
        if (createOutcome.FatalError is { } fatalCreate)
        {
            return Result.Failure(fatalCreate);
        }

        if (IndicatesElevationDenied(createOutcome.ExitCode, createOutcome.StdErr))
        {
            return Result.Failure(ElevationError);
        }

        if (createOutcome.ExitCode != 0)
        {
            return Result.Failure(
                ToolError("DaemonScCreate", "sc create failed.", createOutcome.StdErr, createOutcome.ExitCode));
        }
        RunOutcome startOutcome = await RunScAsync(
            ["start", ServiceName],
            cancellationToken).ConfigureAwait(false);
        if (startOutcome.FatalError is { } fatalStart)
        {
            return Result.Failure(fatalStart);
        }

        if (IndicatesElevationDenied(startOutcome.ExitCode, startOutcome.StdErr))
        {
            return Result.Failure(ElevationError);
        }

        if (startOutcome.ExitCode != 0)
        {
            return Result.Failure(
                ToolError("DaemonScStart", "sc start failed.", startOutcome.StdErr, startOutcome.ExitCode));
        }

        return Result.Success();
    }

    public async Task<Result> UninstallAsync(CancellationToken cancellationToken)
    {
        RunOutcome stopOutcome = await RunScAsync(
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
        RunOutcome deleteOutcome = await RunScAsync(
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
        RunOutcome queryOutcome = await RunScAsync(
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

    private static async Task<RunOutcome> RunScAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ScExePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process();
        process.StartInfo = startInfo;
        try
        {
            process.Start();
        }
        catch (UnauthorizedAccessException)
        {
            return new RunOutcome(-1, string.Empty, string.Empty, ElevationError);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            return new RunOutcome(-1, string.Empty, string.Empty, ElevationError);
        }
        catch (Win32Exception ex)
        {
            return new RunOutcome(
                -1,
                string.Empty,
                string.Empty,
                new Error("DaemonScStart", ex.Message));
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            return new RunOutcome(-1, string.Empty, string.Empty, ElevationError);
        }
        catch (UnauthorizedAccessException)
        {
            return new RunOutcome(-1, string.Empty, string.Empty, ElevationError);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        return new RunOutcome(process.ExitCode, stdout, stderr, null);
    }

    private sealed record RunOutcome(int ExitCode, string StdOut, string StdErr, Error? FatalError);
}
