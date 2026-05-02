using System.Diagnostics;

using System.Text;

using RetroDownfall.Arcanum.Core.Hosting;

using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Infrastructure.Hosting;

public sealed class LinuxDaemonManager : IDaemonManager
{

    internal const string ServiceUnitFileName = "arcanum.service";

    private static readonly string ServiceUnitPath = Path.Combine(

        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),

        ".config",

        "systemd",

        "user",

        ServiceUnitFileName);

    private static readonly Error ContainerUnsupportedError = new(

        "ContainerUnsupported",

        "Daemon management is not supported inside containerized environments. Run 'arcanum serve' as the container entrypoint.");

    private const string NotLoadedMessage = "Daemon is not currently loaded.";

    private const string RunningMessage = "Arcanum daemon is running.";

    public Task<Result> InstallAsync(CancellationToken cancellationToken)
    {

        if (IsRunningInContainer())
        {

            return Task.FromResult(Result.Failure(ContainerUnsupportedError));

        }

        return InstallCoreAsync(cancellationToken);

    }

    public Task<Result> UninstallAsync(CancellationToken cancellationToken)
    {

        if (IsRunningInContainer())
        {

            return Task.FromResult(Result.Failure(ContainerUnsupportedError));

        }

        return UninstallCoreAsync(cancellationToken);

    }

    public Task<Result<string>> GetStatusAsync(CancellationToken cancellationToken)
    {

        if (IsRunningInContainer())
        {

            return Task.FromResult(Result<string>.Failure(ContainerUnsupportedError));

        }

        return GetStatusCoreAsync(cancellationToken);

    }

    private static bool IsRunningInContainer()
    {

        if (File.Exists("/.dockerenv"))
        {

            return true;

        }

        return string.Equals(

            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),

            "true",

            StringComparison.OrdinalIgnoreCase);

    }

    private static async Task<Result> InstallCoreAsync(CancellationToken cancellationToken)
    {

        string? processPath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(processPath))
        {

            return Result.Failure(new Error("DaemonProcessPath", "Could not resolve the current executable path."));

        }

        string? directory = Path.GetDirectoryName(ServiceUnitPath);

        if (string.IsNullOrEmpty(directory))
        {

            return Result.Failure(new Error("DaemonUnitPath", "Invalid systemd user unit path."));

        }

        Directory.CreateDirectory(directory);

        string unitContent = BuildUserUnitFileContent(processPath);

        Result writeResult = await WriteServiceUnitAtomicallyAsync(directory, unitContent, cancellationToken)

            .ConfigureAwait(false);

        if (writeResult.IsFailure)
        {

            return writeResult;

        }

        (int reloadExit, string _, string reloadStderr) = await RunProcessAsync(

            "systemctl",

            ["--user", "daemon-reload"],

            cancellationToken).ConfigureAwait(false);

        if (reloadExit != 0)
        {

            return Result.Failure(ToolError("DaemonSystemdReload", "systemctl --user daemon-reload failed.", reloadStderr, reloadExit));

        }

        (int enableExit, string _, string enableStderr) = await RunProcessAsync(

            "systemctl",

            ["--user", "enable", "--now", ServiceUnitFileName],

            cancellationToken).ConfigureAwait(false);

        if (enableExit != 0)
        {

            return Result.Failure(

                ToolError("DaemonSystemdEnable", "systemctl --user enable --now failed.", enableStderr, enableExit));

        }

        return Result.Success();

    }

    private static async Task<Result> UninstallCoreAsync(CancellationToken cancellationToken)
    {

        _ = await RunProcessAsync(

            "systemctl",

            ["--user", "disable", "--now", ServiceUnitFileName],

            cancellationToken).ConfigureAwait(false);

        try
        {

            if (File.Exists(ServiceUnitPath))
            {

                File.Delete(ServiceUnitPath);

            }

        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            return Result.Failure(new Error("DaemonUnitDelete", ex.Message));

        }

        _ = await RunProcessAsync(

            "systemctl",

            ["--user", "daemon-reload"],

            cancellationToken).ConfigureAwait(false);

        return Result.Success();

    }

    private static async Task<Result<string>> GetStatusCoreAsync(CancellationToken cancellationToken)
    {

        if (!File.Exists(ServiceUnitPath))
        {

            return Result<string>.Success(NotLoadedMessage);

        }

        (int exitCode, string stdout, string _) = await RunProcessAsync(

            "systemctl",

            ["--user", "show", "-p", "ActiveState", "--value", ServiceUnitFileName],

            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {

            return Result<string>.Success(NotLoadedMessage);

        }

        string state = stdout.Trim();

        if (string.Equals(state, "active", StringComparison.Ordinal))
        {

            return Result<string>.Success(RunningMessage);

        }

        return Result<string>.Success(NotLoadedMessage);

    }

    private static string BuildUserUnitFileContent(string processPath)
    {

        string execArgument = FormatExecStartArgument(processPath);

        return $"""

[Unit]

Description=Arcanum Daemon

[Service]

ExecStart={execArgument}

Restart=always

[Install]

WantedBy=default.target

""";

    }

    /// <summary>

    /// Builds the value after <c>ExecStart=</c>: executable path plus <c>serve</c>, with systemd-compatible quoting when needed.

    /// </summary>

    private static string FormatExecStartArgument(string executablePath)
    {

        if (executablePath.AsSpan().IndexOfAny(" \t\"\\".AsSpan()) < 0)
        {

            return $"{executablePath} serve";

        }

        var builder = new StringBuilder(capacity: executablePath.Length + 16);

        _ = builder.Append('"');

        foreach (char c in executablePath)
        {

            if (c is '\\' or '"')
            {

                _ = builder.Append('\\');

            }

            _ = builder.Append(c);

        }

        _ = builder.Append("\" serve");

        return builder.ToString();

    }

    private static async Task<Result> WriteServiceUnitAtomicallyAsync(

        string directory,

        string unitContent,

        CancellationToken cancellationToken)
    {

        string tempPath = Path.Combine(directory, $".{ServiceUnitFileName}.{Guid.NewGuid():N}.tmp");

        try
        {

            await File.WriteAllTextAsync(tempPath, unitContent, cancellationToken).ConfigureAwait(false);

            File.Move(tempPath, ServiceUnitPath, overwrite: true);

        }

        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {

            TryDeleteQuiet(tempPath);

            return Result.Failure(new Error("DaemonUnitWrite", ex.Message));

        }

        return Result.Success();

    }

    private static void TryDeleteQuiet(string path)
    {

        try
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

        catch (IOException)
        {

        }

        catch (UnauthorizedAccessException)
        {

        }

    }

    private static Error ToolError(string code, string message, string stderr, int exitCode)
    {

        string trimmed = stderr.Trim();

        string suffix = string.IsNullOrEmpty(trimmed) ? $"Exit code {exitCode}." : trimmed;

        return new Error(code, $"{message} {suffix}".Trim());

    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(

        string fileName,

        string[] arguments,

        CancellationToken cancellationToken)
    {

        var startInfo = new ProcessStartInfo
        {

            FileName = fileName,

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

        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string stdout = await stdoutTask.ConfigureAwait(false);

        string stderr = await stderrTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);

    }

}
